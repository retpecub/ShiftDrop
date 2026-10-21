@echo off
chcp 65001 >nul
setlocal

where dotnet >nul 2>nul
if errorlevel 1 (
  echo Нужен .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
  exit /b 1
)

echo Сборка ShiftDrop...
dotnet publish "%~dp0src\ShiftDrop\ShiftDrop.csproj" -c Release -o "%~dp0publish"
if errorlevel 1 (
  echo.
  echo Сборка не удалась.
  exit /b 1
)

echo.
echo Готово: %~dp0publish\ShiftDrop.exe
echo Запустите его один раз — дальше он сам добавится в автозагрузку и в меню Проводника.
