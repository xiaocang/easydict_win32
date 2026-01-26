# Easydict Win32 Build System
# Requires: .NET 8.0 SDK
# Optional: WinApp CLI (for MSIX packaging)

.PHONY: all build build-release build-debug test clean restore run help
.PHONY: test-translation test-winui test-verbose run-release
.PHONY: publish publish-x64 publish-x86 publish-arm64
.PHONY: msix msix-x64 msix-x86 msix-arm64 msix-all
.PHONY: cert-generate cert-install sign sign-all

# Certificate configuration
# CERT_PASSWORD must be explicitly provided for cert-related targets
CERT_PATH ?= ./certs/dev.pfx

# Default version for local builds
VERSION ?= 0.1.0.0

# Manifest source path
MANIFEST_SRC := src/Easydict.WinUI/Package.appxmanifest

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
	rm -rf publish msix

# Publish for distribution (portable)
publish: restore
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --output ./publish --self-contained true

# Publish for specific platform
publish-x64: restore
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --runtime win-x64 --self-contained true --output ./publish/x64

publish-x86: restore
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --runtime win-x86 --self-contained true --output ./publish/x86

publish-arm64: restore
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --runtime win-arm64 --self-contained true --output ./publish/arm64

# ============================================================
# MSIX Packaging (requires WinApp CLI: winget install Microsoft.winappcli)
# ============================================================

# Generate development certificate
# Usage: make cert-generate CERT_PASSWORD=yourpassword
cert-generate:
ifndef CERT_PASSWORD
	$(error CERT_PASSWORD is required. Usage: make cert-generate CERT_PASSWORD=yourpassword)
endif
	@mkdir -p certs
	@echo "Generating certificate..."
	@winapp cert generate --publisher "CN=xiaocang" --output $(CERT_PATH) --password "$(CERT_PASSWORD)"
	@echo "Certificate saved to: $(CERT_PATH)"
	@echo "Remember your password for cert-install and sign targets."

# Install certificate to local machine (requires admin)
# Usage: make cert-install CERT_PASSWORD=yourpassword
cert-install:
ifndef CERT_PASSWORD
	$(error CERT_PASSWORD is required. Usage: make cert-install CERT_PASSWORD=yourpassword)
endif
	@winapp cert install --certificate $(CERT_PATH) --password "$(CERT_PASSWORD)"

# Create MSIX package for x64
# Uses shell-local variable to avoid race conditions with parallel builds
msix-x64: publish-x64
	@mkdir -p msix
	@TMP_MANIFEST=$$(mktemp); \
	echo "Creating temp manifest for x64 architecture..."; \
	cp $(MANIFEST_SRC) $$TMP_MANIFEST; \
	sed -i 's/ProcessorArchitecture="[^"]*"/ProcessorArchitecture="x64"/' $$TMP_MANIFEST; \
	sed -i 's/Version="[0-9]*\.[0-9]*\.[0-9]*\.[0-9]*"/Version="$(VERSION)"/' $$TMP_MANIFEST; \
	winapp pack \
		--input ./publish/x64 \
		--output ./msix/Easydict-x64.msix \
		--manifest $$TMP_MANIFEST \
		--verbose; \
	rm -f $$TMP_MANIFEST

# Create MSIX package for x86
msix-x86: publish-x86
	@mkdir -p msix
	@TMP_MANIFEST=$$(mktemp); \
	echo "Creating temp manifest for x86 architecture..."; \
	cp $(MANIFEST_SRC) $$TMP_MANIFEST; \
	sed -i 's/ProcessorArchitecture="[^"]*"/ProcessorArchitecture="x86"/' $$TMP_MANIFEST; \
	sed -i 's/Version="[0-9]*\.[0-9]*\.[0-9]*\.[0-9]*"/Version="$(VERSION)"/' $$TMP_MANIFEST; \
	winapp pack \
		--input ./publish/x86 \
		--output ./msix/Easydict-x86.msix \
		--manifest $$TMP_MANIFEST \
		--verbose; \
	rm -f $$TMP_MANIFEST

# Create MSIX package for ARM64
msix-arm64: publish-arm64
	@mkdir -p msix
	@TMP_MANIFEST=$$(mktemp); \
	echo "Creating temp manifest for arm64 architecture..."; \
	cp $(MANIFEST_SRC) $$TMP_MANIFEST; \
	sed -i 's/ProcessorArchitecture="[^"]*"/ProcessorArchitecture="arm64"/' $$TMP_MANIFEST; \
	sed -i 's/Version="[0-9]*\.[0-9]*\.[0-9]*\.[0-9]*"/Version="$(VERSION)"/' $$TMP_MANIFEST; \
	winapp pack \
		--input ./publish/arm64 \
		--output ./msix/Easydict-arm64.msix \
		--manifest $$TMP_MANIFEST \
		--verbose; \
	rm -f $$TMP_MANIFEST

# Create MSIX package for current platform (default: x64)
msix: msix-x64

# Create all MSIX packages
msix-all: msix-x64 msix-x86 msix-arm64

# Sign MSIX package (requires certificate)
# Usage: make sign CERT=./certs/dev.pfx CERT_PASSWORD=yourpassword MSIX=./msix/Easydict-x64.msix
sign:
ifndef CERT
	$(error CERT is required. Usage: make sign CERT=./certs/dev.pfx CERT_PASSWORD=yourpassword MSIX=./msix/Easydict-x64.msix)
endif
ifndef CERT_PASSWORD
	$(error CERT_PASSWORD is required. Usage: make sign CERT=./certs/dev.pfx CERT_PASSWORD=yourpassword MSIX=./msix/Easydict-x64.msix)
endif
ifndef MSIX
	$(error MSIX is required. Usage: make sign CERT=./certs/dev.pfx CERT_PASSWORD=yourpassword MSIX=./msix/Easydict-x64.msix)
endif
	@winapp sign \
		--input $(MSIX) \
		--certificate $(CERT) \
		--password "$(CERT_PASSWORD)" \
		--verbose

# Sign all MSIX packages
# Usage: make sign-all CERT=./certs/dev.pfx CERT_PASSWORD=yourpassword
sign-all:
ifndef CERT
	$(error CERT is required. Usage: make sign-all CERT=./certs/dev.pfx CERT_PASSWORD=yourpassword)
endif
ifndef CERT_PASSWORD
	$(error CERT_PASSWORD is required. Usage: make sign-all CERT=./certs/dev.pfx CERT_PASSWORD=yourpassword)
endif
	@winapp sign --input ./msix/Easydict-x64.msix --certificate $(CERT) --password "$(CERT_PASSWORD)"
	@winapp sign --input ./msix/Easydict-x86.msix --certificate $(CERT) --password "$(CERT_PASSWORD)"
	@winapp sign --input ./msix/Easydict-arm64.msix --certificate $(CERT) --password "$(CERT_PASSWORD)"

# ============================================================
# Run application
# ============================================================

# Run the application (Debug)
run: build
	./src/Easydict.WinUI/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Easydict.WinUI.exe

# Run the application (Release)
run-release: build-release
	./src/Easydict.WinUI/bin/Release/net8.0-windows10.0.19041.0/win-x64/Easydict.WinUI.exe

# ============================================================
# Help
# ============================================================

# Show help
help:
	@echo "Easydict Win32 Build Targets:"
	@echo ""
	@echo "Build & Run:"
	@echo "  make              - Build the project (Debug)"
	@echo "  make build        - Build the project (Debug)"
	@echo "  make build-debug  - Build the project (Debug)"
	@echo "  make build-release - Build the project (Release)"
	@echo "  make run          - Run the application (Debug)"
	@echo "  make run-release  - Run the application (Release)"
	@echo ""
	@echo "Testing:"
	@echo "  make test         - Run all unit tests"
	@echo "  make test-translation - Run TranslationService tests only"
	@echo "  make test-winui   - Run WinUI tests only"
	@echo "  make test-verbose - Run tests with verbose output"
	@echo ""
	@echo "Publishing:"
	@echo "  make publish      - Publish for distribution (portable)"
	@echo "  make publish-x64  - Publish for x64"
	@echo "  make publish-x86  - Publish for x86"
	@echo "  make publish-arm64 - Publish for ARM64"
	@echo ""
	@echo "MSIX Packaging (requires WinApp CLI):"
	@echo "  make msix         - Create MSIX package (x64)"
	@echo "  make msix-x64     - Create MSIX package for x64"
	@echo "  make msix-x86     - Create MSIX package for x86"
	@echo "  make msix-arm64   - Create MSIX package for ARM64"
	@echo "  make msix-all     - Create all MSIX packages"
	@echo "  make cert-generate CERT_PASSWORD=... - Generate development certificate"
	@echo "  make cert-install CERT_PASSWORD=...  - Install certificate (requires admin)"
	@echo "  make sign CERT=... CERT_PASSWORD=... MSIX=... - Sign MSIX package"
	@echo "  make sign-all CERT=... CERT_PASSWORD=...      - Sign all MSIX packages"
	@echo ""
	@echo "Variables:"
	@echo "  VERSION=X.Y.Z.W   - Set version for MSIX package (default: 0.1.0.0)"
	@echo "  CERT_PASSWORD=... - Certificate password (required for cert/sign targets)"
	@echo "  CERT_PATH=...     - Certificate file path (default: ./certs/dev.pfx)"
	@echo ""
	@echo "Cleanup:"
	@echo "  make clean        - Clean build artifacts"
	@echo "  make help         - Show this help"
