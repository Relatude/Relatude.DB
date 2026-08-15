:: Temporary deployment solution

@echo off
cls
setlocal

:: Packages built here:
::   - one per .nuspec in this folder, packed with nuget.exe from the Release output
::   - Relatude.DB.Tool, the "relatude" command line tool, packed with dotnet pack
::     (a dotnet tool package bundles its whole dependency closure, so it cannot be
::      described by a .nuspec listing loose dlls like the library packages are)

set "solution=..\Relatude.DB.slnx"
set "toolProject=..\src\Relatude.DB.Console\Relatude.DB.Console.csproj"
set "apikeyFile=..\..\Relatude.DB.Secrets\nuget_apikey.txt"

:: nuget.exe ships next to this script, only fall back to one on the PATH
if exist "%~dp0nuget.exe" (
    set "nugetExe=%~dp0nuget.exe"
) else (
    set "nugetExe=nuget"
)

:: Delete Output folder if it exists
if exist Output (
    echo Deleting old Output folder...
    rmdir /s /q Output
)

:: Find a .csproj file
for %%f in (..\\src\\Relatude.DB.Common\\*.csproj) do (
    set "csproj=%%f"
    goto :found
)

:found
if not defined csproj (
    echo ERROR: No .csproj file found!
    pause
    exit /b 1
)

:: Read version number from the .csproj file
set "version="
for /f "usebackq tokens=3 delims=<>" %%v in (`findstr /ri "<\s*Version\s*>" "%csproj%"`) do (
    set "version=%%v"
    goto :gotVersion
)

:gotVersion
if not defined version (
    echo ERROR: Could not find a ^<Version^> tag in %csproj%!
    pause
    exit /b 1
)

:: trim leading/trailing spaces
for /f "tokens=* delims= " %%A in ("%version%") do set "version=%%A"
:trimEnd
if not "%version:~-1%"==" " goto :trimDone
set "version=%version:~0,-1%"
goto :trimEnd
:trimDone

echo Detected version: %version%

:: Warn if the command line tool carries a different version number
set "toolVersion="
for /f "usebackq tokens=3 delims=<>" %%v in (`findstr /ri "<\s*Version\s*>" "%toolProject%"`) do (
    set "toolVersion=%%v"
    goto :gotToolVersion
)
:gotToolVersion
if defined toolVersion if not "%toolVersion%"=="%version%" (
    echo WARNING: %toolProject% says %toolVersion%, packing it as %version% anyway.
)

:: Prompt the user for the version number
set /p tag=Enter subversion tag (ie: -alpha):

:: If the user pressed enter (tag is empty), set it to -alpha
if "%tag%"=="" set "tag=-alpha"

set "fullVersion=%version%%tag%"
echo.
echo Building and packing %fullVersion% ...
echo.

:: Build the solution
dotnet build %solution% --configuration Release
if errorlevel 1 (
    echo ERROR: Build failed, nothing was packed.
    pause
    exit /b 1
)

:: Pack the library NuGet packages using the entered version
for %%f in (.\*.nuspec) do (
    call :packNuspec "%%f"
    if errorlevel 1 goto :packFailed
)

:: Pack the command line tool. dotnet pack, not nuget pack: PackAsTool in the csproj
:: publishes the tool and bundles every dependency under tools\net8.0\any.
:: This rebuilds the referenced projects with the tagged version, so it runs after the
:: nuspec packing above, which picks its dlls straight out of bin\Release.
echo.
echo Packing %toolProject% ...
dotnet pack "%toolProject%" --configuration Release -p:Version=%fullVersion% --output Output\
if errorlevel 1 goto :packFailed

echo.
echo Packages in Output:
for %%f in (Output\*.nupkg) do echo   %%~nxf
echo.

:: Ask whether to publish NuGets
set /p publish=Do you want to publish the NuGet packages to nuget.org? (Y/N):
if /I NOT "%publish%"=="Y" (
    echo Publishing canceled by user.
    pause
    exit /b 0
)

:: Read API key from apikey.txt
if not exist "%apikeyFile%" (
    echo ERROR: %apikeyFile% not found!
    echo Please create a file named nuget_apikey.txt in the Secrets and put your NuGet API key inside.
    pause
    exit /b 1
)

:: Outside of version control, so never exposed publicly
set /p apikey=<"%apikeyFile%"

:: Push all generated packages to nuget.org
set "pushFailed="
for %%f in (Output\*.nupkg) do (
    call :pushPackage "%%f"
)
if defined pushFailed (
    echo.
    echo ERROR: One or more packages failed to publish, see the output above.
    pause
    exit /b 1
)

echo.
echo Published %fullVersion% to nuget.org.
:: Pause so the window stays open after execution
pause
exit /b 0

:packNuspec
echo.
echo Packing %~nx1 ...
"%nugetExe%" pack %1 -OutputDirectory Output\ -Version %fullVersion%
exit /b %errorlevel%

:pushPackage
echo.
echo Pushing %~nx1 ...
dotnet nuget push %1 --source "https://api.nuget.org/v3/index.json" --api-key %apikey% --skip-duplicate
if errorlevel 1 set "pushFailed=1"
exit /b 0

:packFailed
echo.
echo ERROR: Packing failed, nothing was published.
pause
exit /b 1
