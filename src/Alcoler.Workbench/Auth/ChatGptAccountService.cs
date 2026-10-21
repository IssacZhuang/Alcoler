namespace Alcoler.Workbench.Auth;

public enum ChatGptSignInFailure
{
    Denied,
    Timeout,
    Cancelled,
    Network,
    ServerError,
    Protocol,
    CredentialWriteFailed,
}

public sealed record ChatGptAccountSummary(string? Email, string? Name, string? PlanType);

public sealed record ChatGptAccountStatus(
    bool SignedIn,
    bool Entitled,
    ChatGptAccountSummary? Account,
    IReadOnlyList<string> ModelIds);

public sealed record ChatGptSignInOutcome(
    bool Completed,
    bool Entitled,
    ChatGptAccountSummary? Account,
    IReadOnlyList<string> ModelIds,
    ChatGptSignInFailure? Failure,
    string? Message)
{
    public static readonly IReadOnlyList<string> NoModels = [];

    public static ChatGptSignInOutcome Succeeded(bool entitled, ChatGptAccountSummary account, IReadOnlyList<string> modelIds) =>
        new(true, entitled, account, modelIds, null, null);

    public static ChatGptSignInOutcome Failed(ChatGptSignInFailure reason, string? message = null) =>
        new(false, false, null, NoModels, reason, message);
}

/// <summary>
/// Thrown when plan inference needs a fresh sign-in: credentials are missing,
/// the account is not entitled, or the refresh token was rejected.
/// </summary>
public sealed class SiwcAuthRequiredException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ChatGptSignInTransaction : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly LoopbackCallbackListener _listener;
    private bool _disposed;

    internal string AuthorizeUrl { get; }
    internal Task<ChatGptSignInOutcome> Completion { get; }
    internal CancellationToken Cancelled => _cancellation.Token;

    private ChatGptSignInTransaction(
        string authorizeUrl, Task<ChatGptSignInOutcome> completion, LoopbackCallbackListener listener, CancellationTokenSource cancellation)
    {
        AuthorizeUrl = authorizeUrl;
        Completion = completion;
        _listener = listener;
        _cancellation = cancellation;
    }

    internal static ChatGptSignInTransaction Create(
        string authorizeUrl, LoopbackCallbackListener listener, Func<CancellationToken, Task<ChatGptSignInOutcome>> run)
    {
        var cancellation = new CancellationTokenSource();
        var completion = Task.Run(() => run(cancellation.Token));
        return new ChatGptSignInTransaction(authorizeUrl, completion, listener, cancellation);
    }

    public void Cancel() => _cancellation.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
        _ = _listener.DisposeAsync();
    }
}

/// <summary>
/// Orchestrates the Sign in with ChatGPT flow for the workbench: loopback
/// authorization, token exchange with id_token verification, entitlement and
/// plan model discovery, credential persistence, and request-time refresh.
/// </summary>
public sealed class ChatGptAccountService : IDisposable
{
    private static readonly TimeSpan SignInWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RevocationWindow = TimeSpan.FromSeconds(5);
    // Re-auth is required only for these terminal refresh errors; anything else
    // keeps the credentials and may be retried.
    private static readonly HashSet<string> ReauthorizationErrorCodes =
    [
        "invalid_grant",
        "invalid_refresh_token",
        "token_expired",
        "refresh_token_expired",
        "refresh_token_invalidated",
        "refresh_token_reused",
    ];

    private readonly SiwcCredentialStore _store;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private ChatGptSignInTransaction? _activeTransaction;
    private bool _disposed;

    public ChatGptAccountService(string storePath, HttpMessageHandler? httpHandler = null, TimeProvider? timeProvider = null)
    {
        _store = new SiwcCredentialStore(storePath);
        _http = new HttpClient(httpHandler ?? new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = SiwcProtocol.HttpTimeout,
        };
        _time = timeProvider ?? TimeProvider.System;
    }

    public ChatGptSignInTransaction StartSignIn()
    {
        ChatGptSignInTransaction? previous;
        lock (_gate)
        {
            previous = _activeTransaction;
        }
        previous?.Dispose();

        var hostId = _store.ResolveOrCreateHostId();
        var saved = _store.Load();
        var pkce = SiwcProtocol.CreatePkce();
        var state = SiwcProtocol.CreateState();
        var nonce = SiwcProtocol.CreateNonce();
        var listener = LoopbackCallbackListener.StartAsync(SiwcProtocol.CallbackPath, state, CancellationToken.None).GetAwaiter().GetResult();
        var authorizeUrl = SiwcProtocol.BuildAuthorizeUrl(new SiwcAuthorizeInput(
            listener.CallbackUrl, hostId, nonce, pkce, state,
            saved?.IssuedClientId,
            saved?.Account.Email));
        var transaction = ChatGptSignInTransaction.Create(authorizeUrl, listener,
            ct => RunSignInAsync(listener, saved?.IssuedClientId, hostId, nonce, pkce, ct));
        lock (_gate)
        {
            _activeTransaction = transaction;
        }
        return transaction;
    }

    private async Task<ChatGptSignInOutcome> RunSignInAsync(
        LoopbackCallbackListener listener,
        string? savedIssuedClientId,
        string hostId,
        string nonce,
        SiwcPkce pkce,
        CancellationToken cancelled)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancelled);
        window.CancelAfter(SignInWindow);
        try
        {
            var callbackUrl = await listener.WaitForCallbackAsync(window.Token);
            var parsed = SiwcProtocol.ParseCallbackUrl(callbackUrl, savedIssuedClientId);
            var issuedClientId = parsed.IssuedClientId ?? savedIssuedClientId
                ?? throw new SiwcProtocolException("missing-issued-client-id", "Issued client id missing.");
            var discovery = await SiwcProtocol.FetchDiscoveryAsync(_http, window.Token);
            var tokens = await SiwcProtocol.ExchangeCodeAsync(_http, new SiwcCodeExchange(
                issuedClientId, parsed.Code, pkce.CodeVerifier, listener.CallbackUrl), window.Token);
            if (tokens.IdToken is not { Length: > 0 })
                throw new SiwcIdTokenVerificationException("Token response is missing id_token.");
            var claims = await SiwcProtocol.VerifyIdTokenAsync(_http, discovery, tokens.IdToken, issuedClientId, nonce, window.Token);
            var entitled = SiwcProtocol.HasDirectTokenScope(tokens.Scope);
            // The catalog is fetched before publishing credentials so a stored
            // record never lacks its model list.
            var modelIds = entitled
                ? await SiwcProtocol.FetchModelCatalogAsync(_http, tokens.AccessToken, window.Token)
                : ChatGptSignInOutcome.NoModels;
            var record = SiwcCredentialRecord.FromTokens(claims, hostId, issuedClientId, tokens, modelIds: modelIds);
            try
            {
                _store.Save(record);
            }
            catch (Exception ex)
            {
                throw new SiwcCredentialWriteException(ex.Message, ex);
            }
            return ChatGptSignInOutcome.Succeeded(entitled, new ChatGptAccountSummary(claims.Email, claims.Name, claims.ChatgptPlanType), modelIds);
        }
        catch (Exception ex)
        {
            return Classify(ex, cancelled);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    private static ChatGptSignInOutcome Classify(Exception error, CancellationToken cancelled)
    {
        switch (error)
        {
            case SiwcCallbackDeniedException:
                return ChatGptSignInOutcome.Failed(ChatGptSignInFailure.Denied);
            case SiwcProtocolException or SiwcIdTokenVerificationException or SiwcCredentialWriteException:
                return ChatGptSignInOutcome.Failed(error is SiwcCredentialWriteException
                    ? ChatGptSignInFailure.CredentialWriteFailed : ChatGptSignInFailure.Protocol, error.Message);
            case SiwcNetworkException:
                return ChatGptSignInOutcome.Failed(ChatGptSignInFailure.Network, error.Message);
            case SiwcOAuthException oauth:
                return ChatGptSignInOutcome.Failed(
                    oauth.OAuthErrorCode == "invalid_response" ? ChatGptSignInFailure.Protocol : ChatGptSignInFailure.ServerError,
                    error.Message);
            case OperationCanceledException:
                return ChatGptSignInOutcome.Failed(cancelled.IsCancellationRequested
                    ? ChatGptSignInFailure.Cancelled : ChatGptSignInFailure.Timeout);
            default:
                return ChatGptSignInOutcome.Failed(ChatGptSignInFailure.Network, error.Message);
        }
    }

    public ChatGptAccountStatus GetStatus()
    {
        var record = _store.Load();
        if (record is null)
            return new ChatGptAccountStatus(false, false, null, ChatGptSignInOutcome.NoModels);
        return new ChatGptAccountStatus(true, record.IsEntitled,
            new ChatGptAccountSummary(record.Account.Email, record.Account.Name, record.Account.PlanType),
            record.ModelIds ?? []);
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        ChatGptSignInTransaction? transaction;
        lock (_gate)
        {
            transaction = _activeTransaction;
            _activeTransaction = null;
        }
        transaction?.Dispose();
        var record = _store.Load();
        if (record?.RefreshToken is { } refreshToken)
        {
            try
            {
                using var revocation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                revocation.CancelAfter(RevocationWindow);
                var discovery = await SiwcProtocol.FetchDiscoveryAsync(_http, revocation.Token);
                if (discovery.RevocationEndpoint is { Length: > 0 } endpoint)
                    await SiwcProtocol.RevokeRefreshTokenAsync(_http, endpoint, refreshToken, record.IssuedClientId, revocation.Token);
            }
            catch (Exception)
            {
                // Revocation is best effort; the local record is removed regardless.
            }
        }
        _store.DeleteChatGpt();
    }

    /// <summary>
    /// Resolves the current access token for plan inference, refreshing under a
    /// single-flight gate when near expiry. Fail-closed: missing credentials, an
    /// unentitled account or a rejected refresh token surface as
    /// SiwcAuthRequiredException instead of an unusable token.
    /// </summary>
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var record = _store.Load();
        if (record is null)
            throw new SiwcAuthRequiredException("Sign in with ChatGPT to use this provider.");
        if (!record.IsEntitled)
            throw new SiwcAuthRequiredException("The signed-in ChatGPT account cannot use plan-based inference.");
        if (!record.IsNearExpiry(NowMs()))
            return record.AccessToken;
        await _refreshGate.WaitAsync(ct);
        try
        {
            var current = _store.Load();
            if (current is null)
                throw new SiwcAuthRequiredException("Sign in with ChatGPT to use this provider.");
            if (!current.IsEntitled)
                throw new SiwcAuthRequiredException("The signed-in ChatGPT account cannot use plan-based inference.");
            if (!current.IsNearExpiry(NowMs()))
                return current.AccessToken;
            if (current.RefreshToken is not { Length: > 0 } refreshToken)
                throw new SiwcAuthRequiredException("The stored ChatGPT session has no refresh token. Sign in again.");
            SiwcTokenResponse tokens;
            try
            {
                tokens = await SiwcProtocol.RefreshTokensAsync(_http, current.IssuedClientId, refreshToken, ct);
            }
            catch (SiwcOAuthException ex) when (ReauthorizationErrorCodes.Contains(ex.OAuthErrorCode))
            {
                _store.DeleteChatGpt();
                throw new SiwcAuthRequiredException("The ChatGPT session expired. Sign in again.", ex);
            }
            var next = SiwcCredentialRecord.FromTokens(null, current.HostId, current.IssuedClientId, tokens, previous: current);
            _store.Save(next);
            return next.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ChatGptSignInTransaction? transaction;
        lock (_gate)
        {
            transaction = _activeTransaction;
            _activeTransaction = null;
        }
        transaction?.Dispose();
        _http.Dispose();
        _refreshGate.Dispose();
    }
}

internal sealed class SiwcCredentialWriteException(string message, Exception inner) : Exception(message, inner);
