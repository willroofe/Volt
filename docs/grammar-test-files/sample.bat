@echo off
rem Batch grammar sample
set NAME=Volt
if "%NAME%"=="Volt" (
  echo Running %NAME%
) else (
  goto :done
)

:done
exit /b 0
