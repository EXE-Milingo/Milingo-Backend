$config = Get-Content -Raw (Join-Path $PSScriptRoot "..\appsettings.json") | ConvertFrom-Json
$plans = $config.Subscriptions
$expected = [ordered]@{
    Plus = @{ Amount = 59000; DurationDays = 7 }
    Pro = @{ Amount = 139000; DurationDays = 30 }
    Ultra = @{ Amount = 510000; DurationDays = 365 }
}

$actualNames = @($plans.PSObject.Properties.Name)
$expectedNames = @($expected.Keys)

if (@(Compare-Object $actualNames $expectedNames).Count -ne 0) {
    throw "Subscriptions must contain exactly: Plus, Pro, Ultra."
}

foreach ($name in $expectedNames) {
    $actual = $plans.$name
    $wanted = $expected[$name]

    if ($actual.Amount -ne $wanted.Amount) {
        throw "$name amount must be $($wanted.Amount), got $($actual.Amount)."
    }

    if ($actual.DurationDays -ne $wanted.DurationDays) {
        throw "$name duration must be $($wanted.DurationDays), got $($actual.DurationDays)."
    }
}

Write-Output "Payment plan configuration valid."
