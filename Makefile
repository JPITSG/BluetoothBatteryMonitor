PUBLISH_DIR = bin/Release/net8.0-windows10.0.19041.0/win-x64/publish
RELEASE_DIR = release
EXE = BluetoothBatteryMonitor.exe

.PHONY: all frontend dotnet release clean

all: release

frontend: assets/dist/index.html

assets/node_modules: assets/package.json
	cd assets && npm install

assets/dist/index.html: assets/node_modules $(shell find assets/src -type f) assets/index.html assets/vite.config.ts assets/tailwind.config.ts assets/tsconfig.json
	cd assets && npm run build

dotnet: assets/dist/index.html
	dotnet publish -c Release -r win-x64 --self-contained true \
		-p:PublishSingleFile=true \
		-p:IncludeNativeLibrariesForSelfExtract=true \
		-p:EnableCompressionInSingleFile=true

release: dotnet
	mkdir -p $(RELEASE_DIR)
	cp $(PUBLISH_DIR)/$(EXE) $(RELEASE_DIR)/$(EXE)
	@echo ""
	@echo "Output: $(RELEASE_DIR)/$(EXE)"

clean:
	rm -rf $(RELEASE_DIR)
	rm -rf assets/dist assets/node_modules
	dotnet clean -c Release > /dev/null 2>&1 || true
