@echo off
rem Adds the same-named migration for both providers (SQLite and PostgreSQL), like add-migration.sh. Usage: scripts\db\add-migration.cmd ^<name^>
if "%~1"=="" (echo Usage: %~nx0 ^<migration name^> 1>&2 & exit /b 2)
pushd "%~dp0..\..\backend" || exit /b 1
dotnet tool restore >nul || goto :fail
dotnet tool run dotnet-ef migrations add "%~1" --project src\DynamicModules.Persistence --startup-project src\DynamicModules.Persistence --output-dir Migrations || goto :fail
dotnet tool run dotnet-ef migrations add "%~1" --project src\DynamicModules.Persistence.Postgres --startup-project src\DynamicModules.Persistence.Postgres --output-dir Migrations || goto :fail
popd
echo Done. Run: dotnet test tests\DynamicModules.ApplicationTests --filter "FullyQualifiedName~MigrationConsistencyTests"
exit /b 0
:fail
popd
exit /b 1
