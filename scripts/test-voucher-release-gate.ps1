# Run from repository root, with the API stopped and FlowerShop_Test isolated.
param(
    [ValidateSet('Preflight','ReleaseCandidate')][string]$Phase = 'Preflight',
    [string]$ResultsDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) ('FlowerShop-Voucher-Release-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path 'FlowerShop.slnx')) { throw 'Run from the repository root.' }
if ([string]::IsNullOrWhiteSpace($env:FLOWERSHOP_TEST_CONNECTION_STRING)) { throw 'Set FLOWERSHOP_TEST_CONNECTION_STRING first.' }
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection.ConnectionString = $env:FLOWERSHOP_TEST_CONNECTION_STRING
$catalogs = @(@('Database','Initial Catalog') | Where-Object { $connection.ContainsKey($_) } | ForEach-Object { [string]$connection[$_] })
if ($catalogs.Count -eq 0 -or @($catalogs | Where-Object { $_ -ne 'FlowerShop_Test' }).Count -ne 0) { throw 'Use FlowerShop_Test only.' }
if ($connection.ContainsKey('MultipleActiveResultSets') -and [string]$connection['MultipleActiveResultSets'] -notin @('false','no','0')) { throw 'Disable MARS.' }
$di = Get-Content 'FlowerShop.Application/DependencyInjection.cs' -Raw
$expectedFlag = if ($Phase -eq 'Preflight') { 'false' } else { 'true' }
if ($di -notmatch ('new\s+CheckoutVoucherRollout\(' + $expectedFlag + '\)')) { throw "Rollout does not match phase $Phase." }
if (Test-Path $ResultsDirectory) { throw 'Choose a fresh results directory.' }
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$ResultsDirectory = (Resolve-Path $ResultsDirectory).Path
$project = 'FlowerShop.IntegrationTests/FlowerShop.IntegrationTests.csproj'
function Invoke-DotnetChecked {
    param([string[]]$CommandArgs)
    & dotnet @CommandArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($CommandArgs -join ' ')" }
}
function Invoke-Suite {
    param([string]$Name, [string]$Filter = '', [int]$Expected = 0, [string]$Target = $project)
    $testArgs = @('test',$Target,'--no-build','--results-directory',$ResultsDirectory,'--logger',"trx;LogFileName=$Name.trx")
    if ($Filter) { $testArgs += @('--filter',$Filter) }
    Invoke-DotnetChecked -CommandArgs $testArgs
    $report = Join-Path $ResultsDirectory "$Name.trx"
    if (-not (Test-Path $report)) { throw "Missing test report $Name" }
    [xml]$trx = Get-Content -LiteralPath $report -Raw
    $c = $trx.TestRun.ResultSummary.Counters
    if (-not $c -or [int]$c.total -le 0 -or [int]$c.passed -ne [int]$c.total) { throw "Incomplete or failing report $Name" }
    if ($Expected -gt 0 -and [int]$c.total -ne $Expected) { throw "Unexpected test count in $Name (expected $Expected)." }
    Write-Host "$Name : $($c.passed)/$($c.total) passed"
}
Invoke-DotnetChecked -CommandArgs @('build','FlowerShop.slnx','--no-restore','-warnaserror')
Invoke-Suite -Name 'checkout-http' -Filter 'FullyQualifiedName~VoucherCheckoutHttpTests' -Expected 11
Invoke-Suite -Name 'cancellation-http' -Filter 'FullyQualifiedName~VoucherCancellationHttpTests' -Expected 9
Invoke-Suite -Name 'pricing-integration' -Filter 'FullyQualifiedName~CheckoutPricingIntegrationTests' -Expected $(if ($Phase -eq 'Preflight') { 7 } else { 8 })
Invoke-Suite -Name 'cancellation-concurrency' -Filter 'FullyQualifiedName~VoucherCancellationConcurrencyTests' -Expected 8
for ($i = 1; $i -le 5; $i++) {
    Invoke-Suite -Name "concurrency-$i" -Filter 'FullyQualifiedName~VoucherCancellationConcurrencyTests|FullyQualifiedName~CheckoutVoucherIntegrationTests|FullyQualifiedName~AdditionalTransactionLockTests|FullyQualifiedName~VoucherCheckoutHttpTests|FullyQualifiedName~VoucherCancellationHttpTests'
}
# This repository currently has one test project. Full solution catches additional regressions.
Invoke-Suite -Name 'full-regression' -Target 'FlowerShop.slnx'
Write-Host "Phase: $Phase. Test evidence: $ResultsDirectory"
Write-Host 'Runtime test gate passed; SQL audits and explicit release approval remain required.'
