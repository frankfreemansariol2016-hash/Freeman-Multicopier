<#
    Freeman MULTICOPIER - script de compilacion.

    Hace cuatro cosas, en este orden y sin preguntar:
      0. Genera el icono desde branding\icono.svg. El SVG es la fuente y el .ico es
         un producto de compilacion, no un archivo que alguien edita a mano: asi que
         va aqui y no se puede quedar viejo sin que se note.
      1. Publica el ejecutable de un solo archivo, autocontenido.
      2. Le pasa la bateria de pruebas al propio ejecutable. Si algo falla, aqui se corta
         y no se genera nada: es preferible no entregar un instalador roto a entregarlo.
      3. Monta el instalador con Inno Setup.

    Uso:
      .\build.ps1                 compilacion normal
      .\build.ps1 -SkipTests      saltarse las pruebas (solo para recompilar rapido)
      .\build.ps1 -SkipIcon       usar el .ico que ya hay, sin regenerarlo del SVG
      .\build.ps1 -NoInstaller    solo el ejecutable, sin instalador
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipIcon,
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'

# El SDK se busca primero en el PATH, para que este script tambien funcione en un
# agente de compilacion, donde el SDK no esta en el perfil del usuario. Si no esta en
# el PATH, se mira donde se instalo a mano en este equipo.
$Dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source

if ($Dotnet) {
    $DotnetRoot = Split-Path $Dotnet -Parent
} else {
    $DotnetRoot = "$env:LOCALAPPDATA\Programs\dotnet"
    $Dotnet     = Join-Path $DotnetRoot 'dotnet.exe'

    if (-not (Test-Path $Dotnet)) {
        throw "No se encuentra dotnet.exe ni en el PATH ni en $DotnetRoot. Instala el SDK .NET 10 o ajusta las dos rutas de este script."
    }
}

$env:DOTNET_ROOT           = $DotnetRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO         = '1'
$env:PATH                  = "$DotnetRoot;$env:PATH"

$Project = Join-Path $PSScriptRoot 'src\FreemanMultiCopier\FreemanMultiCopier.csproj'
$Stage   = Join-Path $PSScriptRoot 'build\stage'
$Out     = Join-Path $PSScriptRoot 'build\out'
$Version = '0.1.0'

function Say([string]$message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

# ---------------------------------------------------------------- 0. el icono

$IconTool = Join-Path $PSScriptRoot 'branding\icono\icono.csproj'
$Icon     = Join-Path $PSScriptRoot 'src\FreemanMultiCopier\Resources\app.ico'

if ($SkipIcon) {
    Say 'Omitiendo el icono por peticion'
} else {
    Say 'Generando el icono desde branding\icono.svg'

    # Se regenera siempre, y no solo si falta el archivo, porque el .ico lleva la
    # forma y los colores del SVG metidos dentro. Editar el SVG y compilar sin
    # este paso es la unica forma de que el icono se quede viejo.
    & $Dotnet run --project $IconTool -c Release --no-launch-profile

    if ($LASTEXITCODE -ne 0) { throw "La generacion del icono fallo." }
    if (-not (Test-Path $Icon)) { throw "No se genero el icono en $Icon" }
    Write-Host "    $((Get-Item $Icon).Name)  $([math]::Round((Get-Item $Icon).Length / 1KB, 1)) KB"
}

# ---------------------------------------------------------------- 1. publicacion

Say "Publicando el ejecutable de un solo archivo"

if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Path $Stage -Force | Out-Null

& $Dotnet publish $Project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $Stage

if ($LASTEXITCODE -ne 0) { throw "La publicacion fallo." }

$Exe = Join-Path $Stage 'FreemanMultiCopier.exe'
if (-not (Test-Path $Exe)) { throw "No se genero el ejecutable en $Stage" }

$SizeMb = [math]::Round((Get-Item $Exe).Length / 1MB, 1)
Write-Host "    $($Exe.Split('\')[-1])  $SizeMb MB"

# El icono del programa va dentro del ejecutable, pero el instalador lo necesita como
# archivo suelto. La publicacion lo deja en Resources\, asi que se copia a la raiz del
# escenario que es donde el .iss lo busca.
if (Test-Path $Icon) {
    Copy-Item $Icon (Join-Path $Stage 'app.ico') -Force
} else {
    Write-Host '    AVISO: no se encuentra Resources\app.ico; el instalador ira sin icono.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 2. pruebas

if (-not $SkipTests) {
    Say "Pasando la bateria de pruebas al ejecutable recien publicado"

    # El informe se deja en la carpeta de datos del cliente (Log.DataDirectory), que es
    # lo unico escribible una vez instalado bajo Program Files.
    $Report = Join-Path $env:LOCALAPPDATA 'FreemanMultiCopier\prueba.txt'

    # Start-Process y no el operador & porque el ejecutable es de subsystem GUI: PowerShell
    # no espera a esos, y la prueba se quedaria ejecutandose en segundo plano mientras este
    # script ya da por buena una copia que nadie ha comprobado.
    $proc = Start-Process -FilePath $Exe -ArgumentList '--selftest' -Wait -PassThru

    if (Test-Path $Report) { Get-Content $Report | Write-Host }

    if ($proc.ExitCode -ne 0) {
        throw "Las pruebas fallaron (codigo $($proc.ExitCode)). No se genera instalador."
    }

    Remove-Item $Report -Force -ErrorAction SilentlyContinue
} else {
    Write-Host '    Omitidas por peticion.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 3. instalador

if ($NoInstaller) {
    Say "Hecho. Ejecutable en $Stage"
    return
}

$Iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $Iscc) {
    Write-Host ''
    Write-Host 'AVISO: no se encuentra Inno Setup. Se deja solo el ejecutable.' -ForegroundColor Yellow
    Write-Host "       Instala Inno Setup 6 con:  winget install JRSoftware.InnoSetup"
    Say "Hecho. Ejecutable en $Stage"
    return
}

Say "Montando el instalador"

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Path $Out -Force | Out-Null

$Iss = Join-Path $PSScriptRoot 'installer\FreemanMultiCopier.iss'

& $Iscc "/DAppVersion=$Version" "/DStageDir=$Stage" "/DOutDir=$Out" $Iss

if ($LASTEXITCODE -ne 0) { throw "Inno Setup devolvio $LASTEXITCODE" }

$Setup = Get-ChildItem $Out -Filter '*.exe' | Select-Object -First 1
if (-not $Setup) { throw 'Inno Setup no genero ningun instalador.' }

Say 'Listo'
Get-ChildItem $Out | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
    Format-Table -AutoSize
