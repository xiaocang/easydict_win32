# Easydict Win32 Build System
# Requires: .NET 8.0 SDK
# Optional: WinApp CLI (for MSIX packaging)

.PHONY: all build build-release build-debug test clean restore run help
.PHONY: test-translation test-winui test-ui test-integration test-verbose run-release
.PHONY: test-streaming test-textlayout
.PHONY: pack pack-streaming pack-textlayout
.PHONY: publish publish-x64 publish-x86 publish-arm64
.PHONY: publish-msix-x64 publish-msix-x86 publish-msix-arm64
.PHONY: msix msix-x64 msix-x86 msix-arm64 msix-all
.PHONY: fix-msix-minversion validate-msix encrypt-secret pdf-to-images
.PHONY: cert-generate cert-install sign sign-all
.PHONY: installer-x64 installer-x86 installer-arm64 installer

# Certificate configuration
# CERT_PASSWORD must be explicitly provided for cert-related targets
CERT_PATH ?= ./certs/dev.pfx

# Default version for local builds
VERSION ?= 0.1.0.0
DPI ?= 144
FORMAT ?= png
# Legacy .NET/hybrid publish and MSIX targets must opt in explicitly with
# RUNTIME_PROFILE=hybrid. Leaving this empty keeps ordinary build/test commands
# from silently selecting a retained-worker/runtime-producing profile.
RUNTIME_PROFILE ?=
RUST_ONLY_MSBUILD_PROPS := -p:RuntimeProfile=rust-only -p:BuildWorkerOutputs=false -p:EnableInProcLongDocFallback=false

# WinUI target framework used by Easydict.WinUI.csproj.
WINUI_TFM ?= net8.0-windows10.0.22621.0

# Manifest source path
MANIFEST_SRC := src/Easydict.WinUI/Package.appxmanifest

# Default target
all: build

# Restore NuGet packages
restore:
	dotnet restore $(RUST_ONLY_MSBUILD_PROPS)

# Build (default: Debug)
build: restore
	dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Debug $(RUST_ONLY_MSBUILD_PROPS)

# Build Release
build-release: restore
	dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release $(RUST_ONLY_MSBUILD_PROPS)

# Build Debug (explicit)
build-debug: restore
	dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Debug $(RUST_ONLY_MSBUILD_PROPS)

# Run all tests
test: restore
	dotnet test tests/Easydict.Llm.Streaming.Tests --logger "console;verbosity=minimal"
	dotnet test tests/Polyglot.TextLayout.Tests --logger "console;verbosity=minimal"
	dotnet test tests/Easydict.TranslationService.Tests --logger "console;verbosity=minimal"
	dotnet test tests/Easydict.WinUI.Tests --logger "console;verbosity=minimal" $(RUST_ONLY_MSBUILD_PROPS)

# Run TranslationService tests only
test-translation:
	dotnet test tests/Easydict.TranslationService.Tests --logger "console;verbosity=minimal"

# Run WinUI tests only
test-winui:
	dotnet test tests/Easydict.WinUI.Tests --logger "console;verbosity=minimal" $(RUST_ONLY_MSBUILD_PROPS)

# Run Easydict.Llm.Streaming tests only
test-streaming:
	dotnet test tests/Easydict.Llm.Streaming.Tests --logger "console;verbosity=minimal"

# Run Polyglot.TextLayout tests only
test-textlayout:
	dotnet test tests/Polyglot.TextLayout.Tests --logger "console;verbosity=minimal"

# Run UI automation tests (requires app to be installed or EASYDICT_EXE_PATH set)
test-ui:
	dotnet test tests/Easydict.UIAutomation.Tests --logger "console;verbosity=detailed" --filter "Category=UIAutomation" $(RUST_ONLY_MSBUILD_PROPS)

# Integration test service filter (default: all)
# Available services: google, openai, deepl, deepseek, groq, zhipu, github-models,
#                     gemini, doubao, caiyun, niutrans, bing, youdao, phonetic
# Linguee integration tests are skipped by default because the upstream sample API is unavailable.
SERVICE ?= all

# Run integration tests (all services by default)
# Usage: make test-integration
#        make test-integration SERVICE=google
#        make test-integration SERVICE=openai
test-integration:
ifeq ($(SERVICE),all)
	dotnet test tests/Easydict.TranslationService.Tests --logger "console;verbosity=detailed" --filter "Category=Integration"
else
	dotnet test tests/Easydict.TranslationService.Tests --logger "console;verbosity=detailed" --filter "Category=Integration&Service=$(SERVICE)"
endif

# Run tests with verbose output
test-verbose:
	dotnet test --logger "console;verbosity=detailed" $(RUST_ONLY_MSBUILD_PROPS)

# ============================================================
# NuGet Packaging (for libraries published to NuGet)
# ============================================================

# Pack Easydict.Llm.Streaming
pack-streaming: restore
	dotnet pack src/Easydict.Llm.Streaming/Easydict.Llm.Streaming.csproj --configuration Release --output ./nupkg

# Pack Polyglot.TextLayout
pack-textlayout: restore
	dotnet pack src/Polyglot.TextLayout/Polyglot.TextLayout.csproj --configuration Release --output ./nupkg

# Pack all NuGet libraries
pack: pack-streaming pack-textlayout

# Clean build artifacts
clean:
	dotnet clean
	rm -rf src/Easydict.WinUI/bin src/Easydict.WinUI/obj
	rm -rf src/Easydict.TranslationService/bin src/Easydict.TranslationService/obj
	rm -rf src/Easydict.SidecarClient/bin src/Easydict.SidecarClient/obj
	rm -rf tests/Easydict.TranslationService.Tests/bin tests/Easydict.TranslationService.Tests/obj
	rm -rf tests/Easydict.WinUI.Tests/bin tests/Easydict.WinUI.Tests/obj
	rm -rf tests/Easydict.UIAutomation.Tests/bin tests/Easydict.UIAutomation.Tests/obj
	rm -rf publish publish-msix msix

# Publish legacy .NET/hybrid portable distribution.
# The first rs release uses ../rs/scripts/Package-Portable.ps1 instead.
publish: restore
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "rust-only" ] || [ "$$runtime_profile" = "rustonly" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet publish targets for the first rs release."; \
		echo "Use ../rs/scripts/Package-Portable.ps1 for the rs portable package."; \
		exit 1; \
	fi; \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet publish targets; only hybrid is supported for legacy .NET packaging."; \
		exit 1; \
	fi
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --output ./publish --self-contained true -p:BuildWorkerOutputs=false -p:RuntimeProfile=$(RUNTIME_PROFILE)
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); pwsh -NoProfile -File scripts/Build-RustHelpers.ps1 -Platform x64 -Configuration Release -OutputDir ./publish -RuntimeProfile "$$runtime_profile" -IncludeLegacyRegistrarAlias

# Publish legacy .NET/hybrid portable output for a specific platform.
# Includes Rust NativeBridge/BrowserRegistrar helpers + retained workers.
# Rust helpers are copied beside the app executable so the Rust desktop can run
# easydict_browser_registrar.exe, the legacy WinUI browser service can run
# BrowserHostRegistrar.exe, and the browser manifest can point at
# easydict-native-bridge.exe without publishing the old .NET helper projects.
# Portable / Inno-installer output ships to users who may not have the WindowsAppSDK
# framework package installed, so the workers self-contain everything they need
# (the size penalty here is ~30 MB per worker, accepted as the cost of a portable
# distribution). MSIX strips this back to a single shared .NET 8 runtime — see
# the publish-msix-* targets below.
#
# x86 omits the workers because MuPDF.NET 3.2.12 doesn't ship a stable x86 runtime
# and the worker csproj RuntimeIdentifiers exclude win-x86. Worker settings default
# on, but missing-worker startup is guarded and falls back to the in-proc path.
# OCR recognition is now Rust-native, so release outputs no longer publish the
# old .NET OCR worker.
publish-x64: restore
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "rust-only" ] || [ "$$runtime_profile" = "rustonly" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet publish targets for the first rs release."; \
		echo "Use ../rs/scripts/Package-Portable.ps1 for the rs portable package."; \
		exit 1; \
	fi; \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet publish targets; only hybrid is supported for legacy .NET packaging."; \
		exit 1; \
	fi
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --runtime win-x64 --self-contained true --output ./publish/x64 -p:BuildWorkerOutputs=false -p:EnableInProcLongDocFallback=false -p:RuntimeProfile=$(RUNTIME_PROFILE)
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); pwsh -NoProfile -File scripts/Build-RustHelpers.ps1 -Platform x64 -Configuration Release -OutputDir ./publish/x64 -RuntimeProfile "$$runtime_profile" -IncludeLegacyRegistrarAlias
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "hybrid" ]; then \
		dotnet publish src/Easydict.Workers.LongDoc/Easydict.Workers.LongDoc.csproj --configuration Release --runtime win-x64 --self-contained true --output ./publish/x64/workers/longdoc -p:Platform=x64 -p:RuntimeProfile=$$runtime_profile; \
		dotnet publish src/Easydict.Workers.LocalAi/Easydict.Workers.LocalAi.csproj --configuration Release --runtime win-x64 --self-contained true --output ./publish/x64/workers/localai -p:Platform=x64 -p:RuntimeProfile=$$runtime_profile; \
	else \
		rm -rf ./publish/x64/workers ./publish/x64/dotnet; \
		echo "Skipping retained .NET workers for RustOnly runtime profile."; \
	fi

publish-x86: restore
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "rust-only" ] || [ "$$runtime_profile" = "rustonly" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet publish targets for the first rs release."; \
		echo "Use ../rs/scripts/Package-Portable.ps1 for the rs portable package."; \
		exit 1; \
	fi; \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet publish targets; only hybrid is supported for legacy .NET packaging."; \
		exit 1; \
	fi
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --runtime win-x86 --self-contained true --output ./publish/x86 -p:BuildWorkerOutputs=false -p:EnableInProcLongDocFallback=false -p:RuntimeProfile=$(RUNTIME_PROFILE)
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); pwsh -NoProfile -File scripts/Build-RustHelpers.ps1 -Platform x86 -Configuration Release -OutputDir ./publish/x86 -RuntimeProfile "$$runtime_profile" -IncludeLegacyRegistrarAlias

publish-arm64: restore
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "rust-only" ] || [ "$$runtime_profile" = "rustonly" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet publish targets for the first rs release."; \
		echo "Use ../rs/scripts/Package-Portable.ps1 for the rs portable package."; \
		exit 1; \
	fi; \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet publish targets; only hybrid is supported for legacy .NET packaging."; \
		exit 1; \
	fi
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --runtime win-arm64 --self-contained true --output ./publish/arm64 -p:BuildWorkerOutputs=false -p:EnableInProcLongDocFallback=false -p:RuntimeProfile=$(RUNTIME_PROFILE)
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); pwsh -NoProfile -File scripts/Build-RustHelpers.ps1 -Platform arm64 -Configuration Release -OutputDir ./publish/arm64 -RuntimeProfile "$$runtime_profile" -IncludeLegacyRegistrarAlias
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "hybrid" ]; then \
		dotnet publish src/Easydict.Workers.LongDoc/Easydict.Workers.LongDoc.csproj --configuration Release --runtime win-arm64 --self-contained true --output ./publish/arm64/workers/longdoc -p:Platform=arm64 -p:RuntimeProfile=$$runtime_profile; \
		dotnet publish src/Easydict.Workers.LocalAi/Easydict.Workers.LocalAi.csproj --configuration Release --runtime win-arm64 --self-contained true --output ./publish/arm64/workers/localai -p:Platform=arm64 -p:RuntimeProfile=$$runtime_profile; \
	else \
		rm -rf ./publish/arm64/workers ./publish/arm64/dotnet; \
		echo "Skipping retained .NET workers for RustOnly runtime profile."; \
	fi

# Publish for MSIX (without bundled Windows App SDK DLLs)
# MSIX declares a framework package dependency so bundled DLLs are redundant and cause launch failures.
# Includes Rust NativeBridge/BrowserRegistrar helpers + retained workers published into the same output directory.
publish-msix-x64: restore
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "rust-only" ] || [ "$$runtime_profile" = "rustonly" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet MSIX targets for the first rs release."; \
		echo "Use ../rs/scripts/Package-Portable.ps1 for the rs portable package."; \
		exit 1; \
	fi; \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet MSIX targets; only hybrid is supported for legacy .NET/MSIX packaging."; \
		exit 1; \
	fi
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --runtime win-x64 --self-contained true --output ./publish-msix/x64 -p:BuildWorkerOutputs=false -p:EnableInProcLongDocFallback=false -p:PublishReadyToRun=false -p:WindowsAppSDKSelfContained=false -p:RuntimeProfile=$(RUNTIME_PROFILE)
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); pwsh -NoProfile -File scripts/Build-RustHelpers.ps1 -Platform x64 -Configuration Release -OutputDir ./publish-msix/x64 -RuntimeProfile "$$runtime_profile" -IncludeLegacyRegistrarAlias
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "hybrid" ]; then \
		dotnet publish src/Easydict.Workers.LongDoc/Easydict.Workers.LongDoc.csproj --configuration Release --runtime win-x64 --no-self-contained --output ./publish-msix/x64/workers/longdoc -p:Platform=x64 -p:RuntimeProfile=$$runtime_profile -p:WindowsAppSDKSelfContained=false; \
		dotnet publish src/Easydict.Workers.LocalAi/Easydict.Workers.LocalAi.csproj --configuration Release --runtime win-x64 --no-self-contained --output ./publish-msix/x64/workers/localai -p:Platform=x64 -p:RuntimeProfile=$$runtime_profile -p:WindowsAppSDKSelfContained=false; \
		pwsh -NoProfile -File scripts/Dedupe-WorkerSharedFiles.ps1 -PublishDir ./publish-msix/x64 -RuntimeProfile "$$runtime_profile"; \
		pwsh -NoProfile -File scripts/Extract-DotnetRuntime.ps1 -Rid win-x64 -OutputDir ./publish-msix/x64/dotnet -RuntimeProfile "$$runtime_profile"; \
	else \
		rm -rf ./publish-msix/x64/workers ./publish-msix/x64/dotnet; \
		echo "Skipping retained .NET workers and bundled worker runtime for RustOnly runtime profile."; \
	fi

publish-msix-x86: restore
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "rust-only" ] || [ "$$runtime_profile" = "rustonly" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet MSIX targets for the first rs release."; \
		echo "Use ../rs/scripts/Package-Portable.ps1 for the rs portable package."; \
		exit 1; \
	fi; \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet MSIX targets; only hybrid is supported for legacy .NET/MSIX packaging."; \
		exit 1; \
	fi
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --runtime win-x86 --self-contained true --output ./publish-msix/x86 -p:BuildWorkerOutputs=false -p:EnableInProcLongDocFallback=false -p:PublishReadyToRun=false -p:WindowsAppSDKSelfContained=false -p:RuntimeProfile=$(RUNTIME_PROFILE)
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); pwsh -NoProfile -File scripts/Build-RustHelpers.ps1 -Platform x86 -Configuration Release -OutputDir ./publish-msix/x86 -RuntimeProfile "$$runtime_profile" -IncludeLegacyRegistrarAlias

publish-msix-arm64: restore
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "rust-only" ] || [ "$$runtime_profile" = "rustonly" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet MSIX targets for the first rs release."; \
		echo "Use ../rs/scripts/Package-Portable.ps1 for the rs portable package."; \
		exit 1; \
	fi; \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by dotnet MSIX targets; only hybrid is supported for legacy .NET/MSIX packaging."; \
		exit 1; \
	fi
	dotnet publish src/Easydict.WinUI/Easydict.WinUI.csproj --configuration Release --runtime win-arm64 --self-contained true --output ./publish-msix/arm64 -p:BuildWorkerOutputs=false -p:EnableInProcLongDocFallback=false -p:PublishReadyToRun=false -p:WindowsAppSDKSelfContained=false -p:RuntimeProfile=$(RUNTIME_PROFILE)
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); pwsh -NoProfile -File scripts/Build-RustHelpers.ps1 -Platform arm64 -Configuration Release -OutputDir ./publish-msix/arm64 -RuntimeProfile "$$runtime_profile" -IncludeLegacyRegistrarAlias
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" = "hybrid" ]; then \
		dotnet publish src/Easydict.Workers.LongDoc/Easydict.Workers.LongDoc.csproj --configuration Release --runtime win-arm64 --no-self-contained --output ./publish-msix/arm64/workers/longdoc -p:Platform=arm64 -p:RuntimeProfile=$$runtime_profile -p:WindowsAppSDKSelfContained=false; \
		dotnet publish src/Easydict.Workers.LocalAi/Easydict.Workers.LocalAi.csproj --configuration Release --runtime win-arm64 --no-self-contained --output ./publish-msix/arm64/workers/localai -p:Platform=arm64 -p:RuntimeProfile=$$runtime_profile -p:WindowsAppSDKSelfContained=false; \
		pwsh -NoProfile -File scripts/Dedupe-WorkerSharedFiles.ps1 -PublishDir ./publish-msix/arm64 -RuntimeProfile "$$runtime_profile"; \
		pwsh -NoProfile -File scripts/Extract-DotnetRuntime.ps1 -Rid win-arm64 -OutputDir ./publish-msix/arm64/dotnet -RuntimeProfile "$$runtime_profile"; \
	else \
		rm -rf ./publish-msix/arm64/workers ./publish-msix/arm64/dotnet; \
		echo "Skipping retained .NET workers and bundled worker runtime for RustOnly runtime profile."; \
	fi

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
	@winapp cert generate --publisher "CN=33FC47D7-8283-45FC-BB5D-297D1476BB29" --output $(CERT_PATH) --password "$(CERT_PASSWORD)"
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
msix-x64: publish-msix-x64
	@mkdir -p msix
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by make msix-x64; only hybrid is supported for legacy .NET/MSIX packaging."; \
		exit 1; \
	fi; \
	pwsh -NoProfile -File scripts/Package-Msix.ps1 -Platform x64 -PublishDir ./publish-msix/x64 -ManifestPath $(MANIFEST_SRC) -OutputMsixPath ./msix/Easydict-x64.msix -MsixVersion $(VERSION) -RuntimeProfile "$$runtime_profile"
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ -n "$$runtime_profile" ]; then \
		cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- ./msix/Easydict-x64.msix --runtime-profile "$$runtime_profile" --allow-unsigned; \
	else \
		cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- ./msix/Easydict-x64.msix --allow-unsigned; \
	fi

# Create MSIX package for x86
msix-x86: publish-msix-x86
	@mkdir -p msix
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by make msix-x86; only hybrid is supported for legacy .NET/MSIX packaging."; \
		exit 1; \
	fi; \
	pwsh -NoProfile -File scripts/Package-Msix.ps1 -Platform x86 -PublishDir ./publish-msix/x86 -ManifestPath $(MANIFEST_SRC) -OutputMsixPath ./msix/Easydict-x86.msix -MsixVersion $(VERSION) -RuntimeProfile "$$runtime_profile"
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ -n "$$runtime_profile" ]; then \
		cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- ./msix/Easydict-x86.msix --runtime-profile "$$runtime_profile" --allow-unsigned; \
	else \
		cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- ./msix/Easydict-x86.msix --allow-unsigned; \
	fi

# Create MSIX package for ARM64
msix-arm64: publish-msix-arm64
	@mkdir -p msix
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by make msix-arm64; only hybrid is supported for legacy .NET/MSIX packaging."; \
		exit 1; \
	fi; \
	pwsh -NoProfile -File scripts/Package-Msix.ps1 -Platform arm64 -PublishDir ./publish-msix/arm64 -ManifestPath $(MANIFEST_SRC) -OutputMsixPath ./msix/Easydict-arm64.msix -MsixVersion $(VERSION) -RuntimeProfile "$$runtime_profile"
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ -n "$$runtime_profile" ]; then \
		cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- ./msix/Easydict-arm64.msix --runtime-profile "$$runtime_profile" --allow-unsigned; \
	else \
		cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- ./msix/Easydict-arm64.msix --allow-unsigned; \
	fi

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
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ -n "$$runtime_profile" ]; then \
		cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- "$(MSIX)" --runtime-profile "$$runtime_profile" --allow-unsigned; \
	else \
		cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- "$(MSIX)" --allow-unsigned; \
	fi
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
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	for msix in ./msix/Easydict-x64.msix ./msix/Easydict-x86.msix ./msix/Easydict-arm64.msix; do \
		if [ -n "$$runtime_profile" ]; then \
			cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- "$$msix" --runtime-profile "$$runtime_profile" --allow-unsigned; \
		else \
			cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- "$$msix" --allow-unsigned; \
		fi; \
		winapp sign --input "$$msix" --certificate $(CERT) --password "$(CERT_PASSWORD)"; \
	done

# Fix MSIX MinVersion (workaround for WindowsAppSDK #5598)
# Usage: make fix-msix-minversion MSIX=./msix/Easydict-x64.msix
fix-msix-minversion:
ifndef MSIX
	$(error MSIX is required. Usage: make fix-msix-minversion MSIX=./msix/Easydict-x64.msix)
endif
	@pwsh -ExecutionPolicy Bypass -File scripts/Fix-MsixMinVersion.ps1 -MsixPath "$(MSIX)"

# Validate MSIX identity, MinVersion, signature presence, and runtime payload profile.
# Usage: make validate-msix MSIX=./msix/Easydict-x64.msix [RUNTIME_PROFILE=hybrid|rust-only]
# Omitting RUNTIME_PROFILE lets easydict_msix_validate use its Rust-only default.
validate-msix:
ifndef MSIX
	$(error MSIX is required. Usage: make validate-msix MSIX=./msix/Easydict-x64.msix)
endif
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ -n "$$runtime_profile" ]; then \
		cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- "$(MSIX)" --runtime-profile "$$runtime_profile"; \
	else \
		cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate -- "$(MSIX)"; \
	fi

# Encrypt a built-in secret for EncryptedSecrets.json.
# Usage: make encrypt-secret SECRET=your-secret
encrypt-secret:
ifndef SECRET
	$(error SECRET is required. Usage: make encrypt-secret SECRET=your-secret)
endif
	@cargo run --manifest-path ../rs/Cargo.toml -p easydict_encrypt_secret -- "$(SECRET)"

# Render PDF pages to PNG/JPG images for diagnostics.
# Usage: make pdf-to-images PDF=./docs/paper.pdf [OUT=./pages] [DPI=144] [FORMAT=png]
pdf-to-images:
ifndef PDF
	$(error PDF is required. Usage: make pdf-to-images PDF=./docs/paper.pdf [OUT=./pages] [DPI=144] [FORMAT=png])
endif
	@pwsh -NoProfile -File scripts/pdf-to-images.ps1 "$(PDF)" "$(OUT)" -Dpi $(DPI) -Format "$(FORMAT)"

# ============================================================
# EXE Installer (requires Inno Setup 6: https://jrsoftware.org/isinfo.php)
# ============================================================

# Create EXE installer for x64
installer-x64: publish-x64
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by make installer-x64; only hybrid is supported for legacy .NET installer packaging."; \
		exit 1; \
	fi; \
	pwsh -ExecutionPolicy Bypass -File scripts/Build-Installer.ps1 -Platform x64 -Version $(VERSION) -RuntimeProfile "$$runtime_profile"

# Create EXE installer for x86
installer-x86: publish-x86
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by make installer-x86; only hybrid is supported for legacy .NET installer packaging."; \
		exit 1; \
	fi; \
	pwsh -ExecutionPolicy Bypass -File scripts/Build-Installer.ps1 -Platform x86 -Version $(VERSION) -RuntimeProfile "$$runtime_profile"

# Create EXE installer for ARM64
installer-arm64: publish-arm64
	@runtime_profile=$$(printf '%s' "$(RUNTIME_PROFILE)" | tr '[:upper:]' '[:lower:]' | tr '_' '-'); \
	if [ "$$runtime_profile" != "hybrid" ]; then \
		echo "RUNTIME_PROFILE=$(RUNTIME_PROFILE) is not supported by make installer-arm64; only hybrid is supported for legacy .NET installer packaging."; \
		exit 1; \
	fi; \
	pwsh -ExecutionPolicy Bypass -File scripts/Build-Installer.ps1 -Platform arm64 -Version $(VERSION) -RuntimeProfile "$$runtime_profile"

# Create EXE installer for default platform (x64)
installer: installer-x64

# ============================================================
# Run application
# ============================================================

# Run the application (Debug)
run: build
	./src/Easydict.WinUI/bin/Debug/$(WINUI_TFM)/win-x64/Easydict.WinUI.exe

# Run the application (Release)
run-release: build-release
	./src/Easydict.WinUI/bin/Release/$(WINUI_TFM)/win-x64/Easydict.WinUI.exe

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
	@echo "  make test-ui      - Run UI automation tests (requires installed app)"
	@echo "  make test-integration - Run integration tests (all services)"
	@echo "  make test-integration SERVICE=google - Run integration tests for specific service"
	@echo "  make test-verbose - Run tests with verbose output"
	@echo ""
	@echo "Publishing (legacy .NET/hybrid portable, with bundled Windows App SDK):"
	@echo "  make publish      - Publish legacy .NET/hybrid portable output"
	@echo "  make publish-x64  - Publish legacy .NET/hybrid portable x64"
	@echo "  make publish-x86  - Publish legacy .NET/hybrid portable x86"
	@echo "  make publish-arm64 - Publish legacy .NET/hybrid portable ARM64"
	@echo ""
	@echo "Publishing (MSIX, without bundled Windows App SDK):"
	@echo "  make publish-msix-x64  - Publish for x64 (MSIX)"
	@echo "  make publish-msix-x86  - Publish for x86 (MSIX)"
	@echo "  make publish-msix-arm64 - Publish for ARM64 (MSIX)"
	@echo ""
	@echo "MSIX Packaging (requires WinApp CLI):"
	@echo "  make msix         - Create MSIX package (x64)"
	@echo "  make msix-x64     - Create MSIX package for x64"
	@echo "  make msix-x86     - Create MSIX package for x86"
	@echo "  make msix-arm64   - Create MSIX package for ARM64"
	@echo "  make msix-all     - Create all MSIX packages"
	@echo "  make validate-msix MSIX=... [RUNTIME_PROFILE=hybrid|rust-only] - Validate package payload (default: rust-only)"
	@echo "  make cert-generate CERT_PASSWORD=... - Generate development certificate"
	@echo "  make cert-install CERT_PASSWORD=...  - Install certificate (requires admin)"
	@echo "  make sign CERT=... CERT_PASSWORD=... MSIX=... - Sign MSIX package"
	@echo "  make sign-all CERT=... CERT_PASSWORD=...      - Sign all MSIX packages"
	@echo "  make fix-msix-minversion MSIX=...             - Fix MinVersion in MSIX (WindowsAppSDK #5598)"
	@echo "  make encrypt-secret SECRET=...                - Encrypt a built-in secret with the Rust helper"
	@echo "  make pdf-to-images PDF=...                    - Render PDF pages with the Rust helper"
	@echo ""
	@echo "EXE Installer (requires Inno Setup 6):"
	@echo "  make installer      - Create EXE installer (x64)"
	@echo "  make installer-x64  - Create EXE installer for x64"
	@echo "  make installer-x86  - Create EXE installer for x86"
	@echo "  make installer-arm64 - Create EXE installer for ARM64"
	@echo ""
	@echo "Variables:"
	@echo "  VERSION=X.Y.Z.W   - Set version for MSIX package (default: 0.1.0.0)"
	@echo "  CERT_PASSWORD=... - Certificate password (required for cert/sign targets)"
	@echo "  CERT_PATH=...     - Certificate file path (default: ./certs/dev.pfx)"
	@echo "  SERVICE=...       - Integration test service filter (default: all)"
	@echo "  RUNTIME_PROFILE=hybrid - Required for dotnet publish/MSIX/installer targets; rs portable uses ../rs/scripts/Package-Portable.ps1"
	@echo "                      Available: google, openai, deepl, deepseek, groq, zhipu,"
	@echo "                      github-models, gemini, doubao, caiyun, niutrans,"
	@echo "                      bing, youdao, phonetic"
	@echo "                      Linguee tests are skipped by default (upstream unavailable)."
	@echo ""
	@echo "Cleanup:"
	@echo "  make clean        - Clean build artifacts"
	@echo "  make help         - Show this help"
