param(
    [ValidateRange(1, 1000)]
    [int] $Requests = 20,

    [ValidateRange(1, 32)]
    [int] $ClientCount = 4,

    [ValidateSet("RoundRobin", "Weighted", "HealthAware")]
    [string] $Mode = "RoundRobin",

    [ValidateRange(1, 65535)]
    [int] $Port = 8080,

    [switch] $Unstable
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "CloudHttp.Sample.csproj"
$endpoint = if ($Unstable) { "/unstable" } else { "/whoami" }
$failEvery = if ($Unstable) { "3" } else { "0" }

$previousAspNetCoreUrls = $env:ASPNETCORE_URLS
$previousInstanceName = $env:INSTANCE_NAME
$previousFailEvery = $env:FAIL_EVERY
$previousUpstreamUrl = $env:UPSTREAM_URL
$previousRequests = $env:REQUESTS
$previousClientCount = $env:CLIENT_COUNT
$previousMode = $env:DISTRIBUTION_MODE
$previousEndpoint = $env:ENDPOINT

$server = $null

try {
    $env:ASPNETCORE_URLS = "http://localhost:$Port"
    $env:INSTANCE_NAME = "local-upstream"
    $env:FAIL_EVERY = $failEvery

    Write-Host "Starting local upstream on http://localhost:$Port ..."
    $server = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $project, "--", "server") `
        -NoNewWindow `
        -PassThru

    Start-Sleep -Seconds 3

    if ($server.HasExited) {
        throw "Local upstream exited before the client started."
    }

    $env:UPSTREAM_URL = "http://localhost:$Port"
    $env:REQUESTS = [string]$Requests
    $env:CLIENT_COUNT = [string]$ClientCount
    $env:DISTRIBUTION_MODE = $Mode
    $env:ENDPOINT = $endpoint

    Write-Host ""
    Write-Host "Running local CloudHttp client demo..."
    Write-Host "Upstream: $env:UPSTREAM_URL"
    Write-Host "ClientCount: $ClientCount"
    Write-Host "Requests: $Requests"
    Write-Host "Mode: $Mode"
    Write-Host "Endpoint: $endpoint"
    Write-Host ""
    Write-Host "Note: local mode starts one upstream process. It is good for debugging the sample,"
    Write-Host "but it does not demonstrate multi-replica backend distribution like Docker mode."
    Write-Host ""

    dotnet run --project $project -- client
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
        $server.WaitForExit()
    }

    $env:ASPNETCORE_URLS = $previousAspNetCoreUrls
    $env:INSTANCE_NAME = $previousInstanceName
    $env:FAIL_EVERY = $previousFailEvery
    $env:UPSTREAM_URL = $previousUpstreamUrl
    $env:REQUESTS = $previousRequests
    $env:CLIENT_COUNT = $previousClientCount
    $env:DISTRIBUTION_MODE = $previousMode
    $env:ENDPOINT = $previousEndpoint
}
