#requires -Version 5.1
<#
.SYNOPSIS
Добавляет qBittorrent и µTorrent Classic в список обработчиков MAGNET.
.EXAMPLE
.\Register-MagnetClients.ps1
.EXAMPLE
.\Register-MagnetClients.ps1 -Client uTorrent
#>
[CmdletBinding()]
param(
    [ValidateSet('Both', 'qBittorrent', 'uTorrent')]
    [string]$Client = 'Both'
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Этот скрипт предназначен для Windows.'
}

function Get-ClientExe {
    param(
        [string]$Name,
        [string[]]$CommonPaths
    )

    $exeName = "$Name.exe"
    $candidates = @(
        Get-Process -Name $Name -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Path }
    ) + $CommonPaths

    $found = $candidates |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -First 1

    if ($found) {
        Write-Host "Найден $Name — $found"
    }

    while ($true) {
        $entered = (Read-Host "$Name. Введи полный путь к $exeName, Enter — принять найденный путь или пропустить, если пути нет, дефис — пропустить").Trim()
        if ($entered -eq '-') {
            return
        }
        if (-not $entered) {
            return $found
        }

        $path = [Environment]::ExpandEnvironmentVariables($entered.Trim('"'))
        if ([IO.Path]::IsPathRooted($path) -and
            [IO.Path]::GetFileName($path) -eq $exeName -and
            (Test-Path -LiteralPath $path -PathType Leaf)) {
            return (Get-Item -LiteralPath $path).FullName
        }
        Write-Warning "Укажи полный путь к существующему файлу $exeName."
    }
}

function Set-RegistryString {
    param(
        [string]$Subkey,
        [AllowEmptyString()][string]$Name,
        [AllowEmptyString()][string]$Value
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($Subkey)
    try {
        $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $key.Dispose()
    }
}

function Register-MagnetClient {
    param(
        [string]$Name,
        [string]$DisplayName,
        [string]$Path
    )

    $progId = "MagnetClients.$Name.Url"
    $handler = "Software\Classes\$progId"
    $capabilities = "Software\MagnetClients\$Name\Capabilities"

    Set-RegistryString $handler '' 'URL:Magnet URI'
    Set-RegistryString $handler 'URL Protocol' ''
    Set-RegistryString "$handler\DefaultIcon" '' ('"{0}",0' -f $Path)
    Set-RegistryString "$handler\shell\open\command" '' ('"{0}" "%1"' -f $Path)
    Set-RegistryString $capabilities 'ApplicationName' $DisplayName
    Set-RegistryString $capabilities 'ApplicationDescription' "Открывает magnet-ссылки в $Name."
    Set-RegistryString "$capabilities\URLAssociations" 'magnet' $progId

    # Создаём описание протокола только при отсутствии существующей регистрации.
    $magnet = [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey('magnet')
    if ($magnet) {
        try {
            $hasProtocol = $magnet.GetValueNames() -contains 'URL Protocol'
        }
        finally {
            $magnet.Dispose()
        }
    } else {
        $hasProtocol = $false
    }
    if (-not $hasProtocol) {
        Set-RegistryString 'Software\Classes\magnet' 'URL Protocol' ''
    }

    Set-RegistryString 'Software\RegisteredApplications' $DisplayName $capabilities
}

$registered = 0
foreach ($name in @('qBittorrent', 'uTorrent')) {
    if ($Client -ne 'Both' -and $Client -ne $name) {
        continue
    }

    $folders = @(
        $env:ProgramFiles
        ${env:ProgramFiles(x86)}
        $env:APPDATA
        "$env:LOCALAPPDATA\Programs"
        $env:LOCALAPPDATA
    ) | Where-Object { $_ }
    $commonPaths = $folders | ForEach-Object { Join-Path $_ "$name\$name.exe" }
    $exePath = Get-ClientExe -Name $name -CommonPaths $commonPaths
    if (-not $exePath) {
        continue
    }

    $label = if ($name -eq 'uTorrent') { 'µTorrent Classic (MAGNET)' } else { 'qBittorrent (MAGNET)' }
    Register-MagnetClient -Name $name -DisplayName $label -Path $exePath
    $registered++
    Write-Host "Добавлен $label."
}

if ($registered -eq 0) {
    Write-Host 'Программы не добавлены. Запусти нужный торрент-клиент или укажи путь к его EXE.'
    return
}

Write-Host 'В параметрах Windows найди MAGNET и выбери программу по умолчанию.'
Start-Process 'ms-settings:defaultapps'
