param([int]$IntervalMs = 1000)

# E1 memory sampler. Emits CSV rows "ts,pid,ppid,name,private,ws" once per
# interval, covering every visible process so process trees can be rebuilt
# offline. private = PrivateMemorySize64 (no cross-process double counting of
# shared pages); ws = WorkingSet64 (reported separately for reference).

[Console]::Out.WriteLine('ts,pid,ppid,name,private,ws')
[Console]::Out.Flush()

while ($true) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    try {
        $meta = @{}
        foreach ($p in Get-CimInstance Win32_Process -ErrorAction SilentlyContinue) {
            $meta[[int]$p.ProcessId] = @{ ppid = [int]$p.ParentProcessId; name = $p.Name }
        }
        $lines = @()
        foreach ($gp in Get-Process -ErrorAction SilentlyContinue) {
            $m = $meta[[int]$gp.Id]
            if ($null -ne $m) {
                $lines += "$ts,$($gp.Id),$($m.ppid),$($m.name),$($gp.PrivateMemorySize64),$($gp.WorkingSet64)"
            }
        }
        foreach ($l in $lines) { [Console]::Out.WriteLine($l) }
        [Console]::Out.Flush()
    } catch { }
    Start-Sleep -Milliseconds $IntervalMs
}
