@echo off

if "%1"=="" (
    echo Current version:
    type VERSION
    echo.
    echo Usage: %0 ^<new-version^>
    echo Example: %0 1.2.0
    exit /b 1
)

set NEW_VERSION=%1

echo Updating version to %NEW_VERSION%...

REM Update VERSION file
echo %NEW_VERSION% > VERSION

REM Update package.json (basic replacement)
powershell -Command "(Get-Content Web\package.json) -replace '\"version\": \".*\"', '\"version\": \"%NEW_VERSION%\"' | Set-Content Web\package.json"

REM Update Dockerfile
powershell -Command "(Get-Content Dockerfile) -replace 'ARG VERSION=.*', 'ARG VERSION=%NEW_VERSION%' | Set-Content Dockerfile"

echo ✅ Version updated to %NEW_VERSION% in:
echo    - VERSION
echo    - Web/package.json
echo    - Dockerfile
echo.
echo Next steps:
echo 1. Commit these changes: git add . ^&^& git commit -m "Bump version to %NEW_VERSION%"
echo 2. Create tag: git tag v%NEW_VERSION%
echo 3. Push: git push ^&^& git push --tags