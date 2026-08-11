@echo off
setlocal
cd /d "%~dp0"

echo Building and starting the PCL.UI.Next rendering playground...
dotnet run --project "PCL.UI.Next.Playground\PCL.UI.Next.Playground.csproj" --configuration Debug
if errorlevel 1 (
    echo.
    echo Playground failed to start. Review the build output above.
    pause
    exit /b 1
)

endlocal
