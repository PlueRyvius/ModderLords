param([ValidateSet('Ready','Exit','Hang','Flood','Overrun','Spawn')][string]$Mode='Ready',[string]$PidFile)
$ErrorActionPreference='Stop'
if ($Mode -eq 'Exit') { exit 23 }
if ($Mode -eq 'Spawn') {
    # Wait until the test harness has assigned this parent to its job.
    $null=[Console]::ReadLine()
    $info=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle='Hidden'
    foreach ($arg in @('-NoProfile','-File',$PSCommandPath,'-Mode','Hang')) { $info.ArgumentList.Add($arg) }
    $child=[Diagnostics.Process]::Start($info)
    [IO.File]::WriteAllText($PidFile,[string]$child.Id)
    exit 0
}
if ($Mode -in @('Flood','Overrun')) {
    $chunk='x'*4096
    $count=if ($Mode -eq 'Overrun') {5000} else {512}
    for ($i=0;$i -lt $count;$i++) { [Console]::Out.WriteLine($chunk); [Console]::Error.WriteLine($chunk) }
}
if ($Mode -eq 'Ready' -or $Mode -eq 'Flood') {
    [Console]::WriteLine('@DS@{"ev":"state","phase":"serving"}')
    [Console]::Out.Flush()
    while (($line=[Console]::ReadLine()) -ne $null) { if ($line -eq 'quit') { exit 0 } }
}
while ($true) { Start-Sleep -Milliseconds 100 }
