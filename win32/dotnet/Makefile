# Easydict Win32 Build System
# Requires: .NET 8.0 SDK

.PHONY: all build build-release build-debug test clean restore run help

# Default target
all: build

# Restore NuGet packages
restore:
	dotnet restore

# Build (default: Debug)
build: restore
	dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Debug

# Build Release
build-release: restore
	dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release

# Build Debug (explicit)
build-debug: restore
	dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Debug

# Run all tests
test: restore
	dotnet test tests/Easydict.TranslationService.Tests --logger "console;verbosity=minimal"
	dotnet test tests/Easydict.WinUI.Tests --logger "console;verbosity=minimal"

# Run TranslationService tests only
test-translation:
	dotnet test tests/Easydict.TranslationService.Tests --logger "console;verbosity=minimal"

# Run WinUI tests only
test-winui:
	dotnet test tests/Easydict.WinUI.Tests --logger "console;verbosity=minimal"

# Run tests with verbose output
test-verbose:
	dotnet test --logger "console;verbosity=detailed"

# Clean build artifacts
clean:
	dotnet clean
	rm -rf src/Easydict.WinUI/bin src/Easydict.WinUI/obj
	rm -rf src/Easydict.TranslationService/bin src/Easydict.TranslationService/obj
	rm -rf src/Easydict.SidecarClient/bin src/Easydict.SidecarClient/obj
	rm -rf tests/Easydict.TranslationService.Tests/bin tests/Easydict.TranslationService.Tests/obj
	rm -rf tests/Easydict.WinUI.Tests/bin tests/Easydict.WinUI.Tests/obj

# Publish for distribution
publish: restore
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --output ./publish --self-contained false

# Run the application (Debug)
run: build
	./src/Easydict.WinUI/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Easydict.WinUI.exe

# Run the application (Release)
run-release: build-release
	./src/Easydict.WinUI/bin/Release/net8.0-windows10.0.19041.0/win-x64/Easydict.WinUI.exe

# Show help
help:
	@echo "Easydict Win32 Build Targets:"
	@echo "  make              - Build the project (Debug)"
	@echo "  make build        - Build the project (Debug)"
	@echo "  make build-debug  - Build the project (Debug)"
	@echo "  make build-release- Build the project (Release)"
	@echo "  make test         - Run all unit tests"
	@echo "  make test-translation - Run TranslationService tests only"
	@echo "  make test-winui   - Run WinUI tests only"
	@echo "  make test-verbose - Run tests with verbose output"
	@echo "  make clean        - Clean build artifacts"
	@echo "  make publish      - Publish for distribution"
	@echo "  make run          - Run the application (Debug)"
	@echo "  make run-release  - Run the application (Release)"
	@echo "  make help         - Show this help"
