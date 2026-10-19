@echo off
rem Convenience launcher: the real script lives next to the project file.
rem Use this one if you are sitting in "LLM Locally" instead of ModelFitStudio.
call "%~dp0ModelFitStudio\rebuild.bat" %*
