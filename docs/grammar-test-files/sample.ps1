# PowerShell grammar sample
param([string]$Name = "Volt")

function Write-Greeting {
    param([string]$Value)
    Write-Host "Hello $Value"
}

Write-Greeting -Value $Name
