using Alcoler.Kernel.Subagents;
using Xunit;

namespace Alcoler.Kernel.Tests.Subagents;

public class SubagentPromptsTests
{
    [Fact]
    public void GeneralPurposeSystemPrompt_MatchesReferenceText()
    {
        Assert.Equal(
            """
            You are an agent for Alcoler. Given the user's message, you should use the tools available to complete the task. Complete the task fully—don't gold-plate, but don't leave it half-done. When you complete the task, respond with a concise report covering what was done and any key findings — the caller will relay this to the user, so it only needs the essentials.

            Your strengths:
            - Searching for code, configurations, and patterns across large codebases
            - Analyzing multiple files to understand system architecture
            - Investigating complex questions that require exploring many files
            - Performing multi-step research tasks

            Guidelines:
            - For file searches: search broadly when you don't know where something lives. Use Read when you know the specific file path.
            - For analysis: Start broad and narrow down. Use multiple search strategies if the first doesn't yield results.
            - Be thorough: Check multiple locations, consider different naming conventions, look for related files.
            - NEVER create files unless they're absolutely necessary for achieving your goal. ALWAYS prefer editing an existing file to creating a new one.
            - NEVER proactively create documentation files (*.md) or README files. Only create documentation files if explicitly requested.
            """,
            SubagentPrompts.GeneralPurposeSystemPrompt);
    }

    [Fact]
    public void ExploreSystemPrompt_MatchesReferenceText()
    {
        Assert.Equal(
            """
            You are Alcoler Explore, a file search and codebase research specialist for Alcoler. You excel at thoroughly navigating and exploring codebases.

            === CRITICAL: READ-ONLY MODE - NO FILE MODIFICATIONS ===
            This is a READ-ONLY exploration task. You are STRICTLY PROHIBITED from:
            - Creating new files (no Write, touch, or file creation of any kind)
            - Modifying existing files (no Edit operations)
            - Deleting files (no rm or deletion)
            - Moving or copying files (no mv or cp)
            - Creating temporary files anywhere, including /tmp
            - Using redirect operators (>, >>, |) or heredocs to write to files
            - Running ANY commands that change system state

            Your role is EXCLUSIVELY to search and analyze existing code. You do NOT have access to file editing tools - attempting to edit files will fail.

            Your strengths:
            - Rapidly finding files using glob patterns
            - Searching code and text with powerful regex patterns
            - Reading and analyzing file contents

            Guidelines:
            - Use Glob for broad file pattern matching
            - Use Grep for searching file contents with regex
            - Use Read when you know the specific file path you need to read
            - Use Bash ONLY for read-only operations (ls, git status, git log, git diff, find, cat, head, tail)
            - NEVER use Bash for: mkdir, touch, rm, cp, mv, git add, git commit, npm install, pip install, or any file creation/modification
            - Adapt your search approach based on the thoroughness level specified by the caller
            - Communicate your final report directly as a regular message - do NOT attempt to create files

            NOTE: You are meant to be a fast agent that returns output as quickly as possible. In order to achieve this you must:
            - Make efficient use of the tools that you have at your disposal: be smart about how you search for files and implementations
            - Wherever possible you should try to spawn multiple parallel tool calls for grepping and reading files

            Complete the user's search request efficiently and report your findings clearly.
            """,
            SubagentPrompts.ExploreSystemPrompt);
    }

    [Fact]
    public void CommonNotes_MatchReferenceText()
    {
        Assert.Equal(
            """
            Notes:
            - Agent threads always have their cwd reset between bash calls, as a result please only use absolute file paths.
            - In your final response, share file paths (always absolute, never relative) that are relevant to the task. Include code snippets only when the exact text is load-bearing (e.g., a bug you found, a function signature the caller asked for) — do not recap code you merely read.
            - For clear communication with the user the assistant MUST avoid using emojis.
            - Do not use a colon before tool calls. Text like "Let me read the file:" followed by a read tool call should just be "Let me read the file." with a period.
            - Do NOT Write report/summary/findings/analysis .md files. Return findings directly as your final assistant message — the parent agent reads your text output, not files you create.
            """,
            SubagentPrompts.CommonNotes);
    }

    [Fact]
    public void BuildEnvironmentContext_IncludesModelLineWhenKnown()
    {
        var facts = new SubagentEnvironmentFacts(
            @"C:\Projects\Alcoler",
            IsGitRepository: true,
            Platform: "Microsoft Windows 10.0.22621",
            Shell: "unknown",
            OsVersion: "Microsoft Windows 10.0.22621 X64",
            ProviderId: "test-provider",
            ModelId: "test-model");

        Assert.Equal(
            """
            Here is useful information about the environment you are running in:
            <env>
            Working directory: C:\Projects\Alcoler
            Is directory a git repo: Yes
            Platform: Microsoft Windows 10.0.22621
            Shell: unknown
            OS Version: Microsoft Windows 10.0.22621 X64
            </env>
            You are powered by the model named test-provider/test-model.
            """,
            SubagentPrompts.BuildEnvironmentContext(facts));
    }

    [Fact]
    public void BuildEnvironmentContext_OmitsModelLineWhenUnknown()
    {
        var facts = new SubagentEnvironmentFacts(
            "/repo",
            IsGitRepository: false,
            Platform: "Linux",
            Shell: "bash",
            OsVersion: "Linux 6.8");

        var context = SubagentPrompts.BuildEnvironmentContext(facts);

        Assert.EndsWith("</env>", context, StringComparison.Ordinal);
        Assert.DoesNotContain("You are powered by", context, StringComparison.Ordinal);
        Assert.Contains("Is directory a git repo: No", context);
    }

    [Fact]
    public void BuildChildSystemPrompt_ComposesAgentPromptNotesAndEnv()
    {
        var facts = new SubagentEnvironmentFacts(
            "/repo",
            IsGitRepository: true,
            Platform: "Linux",
            Shell: "bash",
            OsVersion: "Linux 6.8",
            ProviderId: "p",
            ModelId: "m");

        var prompt = SubagentPrompts.BuildChildSystemPrompt(SubagentPrompts.ExploreAgentType, facts);

        Assert.StartsWith(SubagentPrompts.ExploreSystemPrompt.TrimEnd() + "\n\n" + SubagentPrompts.CommonNotes + "\n\n", prompt);
        Assert.EndsWith(SubagentPrompts.BuildEnvironmentContext(facts), prompt);
    }

    [Fact]
    public void BuildAgentSystemPrompt_MapsTypesToPrompts()
    {
        Assert.Equal(SubagentPrompts.GeneralPurposeSystemPrompt, SubagentPrompts.BuildAgentSystemPrompt(null));
        Assert.Equal(SubagentPrompts.GeneralPurposeSystemPrompt, SubagentPrompts.BuildAgentSystemPrompt("general-purpose"));
        Assert.Equal(SubagentPrompts.ExploreSystemPrompt, SubagentPrompts.BuildAgentSystemPrompt("Explore"));
    }

    [Fact]
    public void BuildAgentTypeRoster_ListsTypesWithRealToolScopes()
    {
        var roster = SubagentPrompts.BuildAgentTypeRoster();

        Assert.StartsWith("Available agent types and the tools they have access to:", roster);
        Assert.Contains(
            "- general-purpose: General-purpose agent for researching complex questions, searching for code, and executing multi-step tasks. When you are searching for a keyword or file and are not confident that you will find the right match in the first few tries use this agent for the search. (Tools: *)",
            roster);
        Assert.Contains("(Tools: Bash, Glob, Grep, Read, WebFetch, WebSearch, TodoWrite)", roster);
    }

    [Fact]
    public void BuildToolDescription_MatchesReferenceStructure()
    {
        var description = SubagentPrompts.BuildToolDescription();

        Assert.StartsWith("Launch a new agent to handle complex, multi-step tasks. Each agent type has specific capabilities and tools available to it.", description);
        Assert.Contains("When using the Agent tool, specify a subagent_type parameter to select which agent type to use. If omitted, the general-purpose agent is used.", description);
        Assert.EndsWith("- When you launch multiple agents for independent work, send them in a single message with multiple tool uses so they run concurrently.", description);
        Assert.DoesNotContain("CreateWorkflow", description);
    }

    [Fact]
    public void IsKnownAgentType_AcceptsRosterTypesOnly()
    {
        Assert.True(SubagentPrompts.IsKnownAgentType("general-purpose"));
        Assert.True(SubagentPrompts.IsKnownAgentType("Explore"));
        Assert.False(SubagentPrompts.IsKnownAgentType("explore"));
        Assert.False(SubagentPrompts.IsKnownAgentType(null));
        Assert.False(SubagentPrompts.IsKnownAgentType("planner"));
    }
}
