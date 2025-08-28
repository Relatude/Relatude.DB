@echo off
cls

:: Delete Output folder if it exists
if exist Output (
    echo Deleting old Output folder...
    rmdir /s /q Output
)

:: Prompt the user for the version number
set /p version=Enter version number (ie: 0.1.0.1-alpha): 

:: Build the solution
dotnet build ..\Relatude.DB.sln --configuration Release

:: Pack the NuGet packages using the entered version
for %%f in (.\*.nuspec) do (
    nuget pack %%f -OutputDirectory Output\ -Version %version%
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

set /p apikey=<../Secrets/nuget_apikey.txt

:: Push all generated packages to nuget.org
for %%f in (Output\*.nupkg) do (
    dotnet nuget push %%f --source "https://api.nuget.org/v3/index.json" --api-key %apikey%
)
:: Pause so the window stays open after execution
pause
