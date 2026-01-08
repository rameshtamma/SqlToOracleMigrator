@echo off
setlocal
set CONFIG=Release
if not "%1"=="" set CONFIG=%1
echo Building SqlToOracleMigrator (%CONFIG%)...
dotnet --version
dotnet restore SqlToOracleMigrator.sln
dotnet build SqlToOracleMigrator.sln -c %CONFIG%
endlocal
