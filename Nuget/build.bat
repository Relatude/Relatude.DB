@echo off
cls

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
    echo ERROR: Could not find a <Version> tag in %csproj%!
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

:: Prompt the user for the version number
set /p tag=Enter subversion tag (ie: -alpha): 

:: Build the solution
dotnet build ..\src\Relatude.DB.sln --configuration Release

:: Pack the NuGet packages using the entered version
for %%f in (.\*.nuspec) do (
    nuget pack %%f -OutputDirectory Output\ -Version %version%%tag%
)

:: Ask whether to publish NuGets
set /p publish=Do you want to publish the NuGet packages to nuget.org? (Y/N):
if /I NOT "%publish%"=="Y" (
    echo Publishing canceled by user.
    pause
    exit /b 0
)

:: Read API key from apikey.txt
if not exist ../Secrets/nuget_apikey.txt (
    echo ERROR: ../Secrets/nuget_apikey.txt not found!
    echo Please create a file named nuget_apikey.txt in the Secrets and put your NuGet API key inside.
    pause
    exit /b 1
)

:: Outside of version control, so never exposed publicly
set /p apikey=<../../Relatude.DB.Secrets/nuget_apikey.txt

:: Push all generated packages to nuget.org
for %%f in (Output\*.nupkg) do (
    dotnet nuget push %%f --source "https://api.nuget.org/v3/index.json" --api-key %apikey%
)
:: Pause so the window stays open after execution
pause
