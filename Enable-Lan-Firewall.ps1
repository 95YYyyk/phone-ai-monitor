$ErrorActionPreference = "Stop"
$log = Join-Path $PSScriptRoot "lan-firewall.log"
try {
    Get-NetFirewallRule -DisplayName ".NET Host" -ErrorAction SilentlyContinue |
        Where-Object { $_.Direction -eq "Inbound" -and $_.Action -eq "Block" -and $_.Enabled -eq "True" } |
        Disable-NetFirewallRule

    $name = "DeskSideDisplay LAN 8767"
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow `
        -Profile Private -Protocol TCP -LocalPort 8767 `
        -Program $dotnet | Out-Null
    "OK" | Set-Content -LiteralPath $log
} catch {
    $_ | Out-String | Set-Content -LiteralPath $log
    exit 1
}
