# Runs a fleet of mock OCPP 1.6J charge points against the Elsewedy Plug test CSMS.
# Ids are <IdPrefix><First>.. <IdPrefix><First+Count-1>, zero-padded to 4 digits, appended to the URL.
# Defaults: CP0006..CP0015, auto-starting and stopping charging sessions.
param(
    [string]$Url = "wss://api.test.elsewedyplug.com/ocpp",  # adjust the path if your CSMS uses a different one
    [string]$IdPrefix = "CP",
    [int]$First = 6,
    [int]$Count = 10,
    [int]$Connectors = 1,
    [double]$PowerW = 7400,
    [string]$User,
    [string]$Password,
    [switch]$Passive,   # connect only; don't auto-start sessions
    [switch]$Wire       # print raw JSON frames
)

$cpArgs = @("--url", $Url, "--id-prefix", $IdPrefix, "--first", $First, "--count", $Count,
            "--connectors", $Connectors, "--power", $PowerW)
if ($User)    { $cpArgs += @("--user", $User, "--pass", $Password) }
if ($Passive) { $cpArgs += "--passive" } else { $cpArgs += "--auto" }
if ($Wire)    { $cpArgs += "--wire" }

Push-Location $PSScriptRoot
try {
    dotnet run --project .\Mock-OCPP-ChargePoint.csproj -- @cpArgs
} finally {
    Pop-Location
}
