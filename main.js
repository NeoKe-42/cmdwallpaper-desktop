const { app, BrowserWindow, screen, Tray, Menu, nativeImage, globalShortcut } = require('electron');
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');

let mainWindow = null;
let helperProcess = null;
let desktopHostProcess = null;
let tray = null;

const IS_WINDOWED = process.argv.includes('--windowed');
let PARENT_MODE = 'progman';
if (process.argv.some(a => a.startsWith('--parent=workerw'))) PARENT_MODE = 'workerw';
if (process.argv.some(a => a.startsWith('--parent=progman'))) PARENT_MODE = 'progman';

const isPackaged = app.isPackaged;
const resourcesDir = isPackaged ? process.resourcesPath : __dirname;

const appDir = path.join(__dirname, 'app');
const dataDir = path.join(appDir, 'data');
const helperExe = path.join(resourcesDir, 'helper', 'cmdwallpaper_agent.exe');
const desktopHostExe = path.join(resourcesDir, 'desktop_host', 'publish', 'desktop_host.exe');

// ── Helper lifecycle ──────────────────────────────────────

function startHelper() {
  if (!fs.existsSync(helperExe)) {
    console.log('Helper not found. Running UI only.');
    return;
  }

  if (!fs.existsSync(dataDir)) {
    fs.mkdirSync(dataDir, { recursive: true });
  }

  helperProcess = spawn(helperExe, ['.'], {
    cwd: appDir,
    windowsHide: true
  });

  helperProcess.stdout.on('data', (data) => {
    console.log('[helper]', data.toString().trimEnd());
  });

  helperProcess.stderr.on('data', (data) => {
    console.error('[helper:err]', data.toString().trimEnd());
  });

  helperProcess.on('error', (err) => {
    console.log('Helper failed to start:', err.message);
    helperProcess = null;
  });

  helperProcess.on('exit', (code) => {
    console.log('Helper exited with code:', code);
    helperProcess = null;
  });
}

function stopHelper() {
  if (helperProcess && !helperProcess.killed) {
    helperProcess.kill();
    helperProcess = null;
  }
}

function stopDesktopHost() {
  if (desktopHostProcess && !desktopHostProcess.killed) {
    desktopHostProcess.kill();
    desktopHostProcess = null;
  }
}

// ── Tray icon (visible 16x16 green square) ────────────────

function createTrayIcon() {
  try {
    const size = 16;
    const buf = Buffer.alloc(size * size * 4);
    for (let i = 0; i < size * size; i++) {
      buf[i * 4]       = 0x4a; // B
      buf[i * 4 + 1]   = 0xc4; // G
      buf[i * 4 + 2]   = 0x6e; // R
      buf[i * 4 + 3]   = 0xff; // A
    }
    return nativeImage.createFromBitmap(buf, { width: size, height: size });
  } catch (e) {
    console.warn('[tray] createFromBitmap failed, using empty icon:', e.message);
    return nativeImage.createEmpty();
  }
}

// ── Force quit ────────────────────────────────────────────

let isQuitting = false;

function forceQuit() {
  if (isQuitting) return;
  isQuitting = true;
  console.log('[app] forceQuit');

  stopDesktopHost();
  stopHelper();

  try { globalShortcut.unregisterAll(); } catch (_) {}

  if (tray) {
    try { tray.destroy(); } catch (_) {}
    tray = null;
  }

  if (mainWindow && !mainWindow.isDestroyed()) {
    try { mainWindow.destroy(); } catch (_) {}
    mainWindow = null;
  }

  console.log('[app] exiting (code 0)');
  app.exit(0);
}

// ── Embed Electron window into desktop ────────────────────

function embedToDesktop(hwnd, bounds, parentMode) {
  if (!fs.existsSync(desktopHostExe)) {
    console.log('desktop_host.exe not found. Run: npm run build:host');
    console.log('Falling back: showing window normally.');
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.setBounds(bounds);
      mainWindow.show();
    }
    return;
  }

  const args = [
    hwnd,
    String(bounds.x),
    String(bounds.y),
    String(bounds.width),
    String(bounds.height),
    '--parent', parentMode
  ];

  console.log('[desktop_host] Launching HWND=' + hwnd + ' parent=' + parentMode);

  desktopHostProcess = spawn(desktopHostExe, args, {
    windowsHide: true,
    stdio: 'pipe'
  });

  desktopHostProcess.stdout.on('data', (data) => {
    console.log('[desktop_host]', data.toString().trimEnd());
  });

  desktopHostProcess.stderr.on('data', (data) => {
    console.error('[desktop_host:err]', data.toString().trimEnd());
  });

  desktopHostProcess.on('error', (err) => {
    console.log('[desktop_host] Failed to start:', err.message);
    desktopHostProcess = null;
  });

  desktopHostProcess.on('exit', (code) => {
    console.log('[desktop_host] Exited with code:', code);
    desktopHostProcess = null;
    if (code !== 0 && mainWindow && !mainWindow.isDestroyed()) {
      console.log('[desktop_host] Embed failed, fallback to normal window');
      mainWindow.setBounds(bounds);
      mainWindow.setSkipTaskbar(true);
      mainWindow.show();
    }
  });
}

// ── Tray menu ─────────────────────────────────────────────

function createTray() {
  const icon = createTrayIcon();
  tray = new Tray(icon);

  const menu = Menu.buildFromTemplate([
    {
      label: 'Reload wallpaper',
      click: () => {
        if (mainWindow) mainWindow.reload();
      }
    },
    { type: 'separator' },
    {
      label: 'Quit',
      click: () => {
        forceQuit();
      }
    }
  ]);

  tray.setToolTip('CMD Wallpaper Desktop');
  tray.setContextMenu(menu);
  console.log('[tray] created with visible icon');
}

// ── Global shortcuts ──────────────────────────────────────

function registerShortcuts() {
  const qOk = globalShortcut.register('Ctrl+Alt+Q', () => {
    console.log('[shortcut] Ctrl+Alt+Q');
    forceQuit();
  });
  console.log('[shortcut] Ctrl+Alt+Q registered:', qOk);

  const q2Ok = globalShortcut.register('Ctrl+Alt+Shift+Q', () => {
    console.log('[shortcut] Ctrl+Alt+Shift+Q');
    forceQuit();
  });
  console.log('[shortcut] Ctrl+Alt+Shift+Q registered:', q2Ok);

  const rOk = globalShortcut.register('Ctrl+Alt+R', () => {
    console.log('[shortcut] Ctrl+Alt+R');
    if (mainWindow) mainWindow.reload();
  });
  console.log('[shortcut] Ctrl+Alt+R registered:', rOk);
}

// ── Window creation ───────────────────────────────────────

function createWindow() {
  if (IS_WINDOWED) {
    mainWindow = new BrowserWindow({
      width: 1280,
      height: 820,
      frame: true,
      resizable: true,
      skipTaskbar: false,
      focusable: true,
      autoHideMenuBar: true,
      backgroundColor: '#282c34',
      webPreferences: {
        preload: path.join(__dirname, 'preload.js'),
        webSecurity: false
      }
    });

    mainWindow.loadFile(path.join(appDir, 'wallpaper.html'));
    mainWindow.once('ready-to-show', () => { mainWindow.show(); });
    mainWindow.on('closed', () => { mainWindow = null; });

    console.log('Mode: windowed (debug)');
  } else {
    const display = screen.getPrimaryDisplay();
    const bounds = display.bounds;

    mainWindow = new BrowserWindow({
      x: bounds.x,
      y: bounds.y,
      width: bounds.width,
      height: bounds.height,
      frame: false,
      resizable: false,
      movable: false,
      minimizable: false,
      maximizable: false,
      fullscreenable: false,
      skipTaskbar: true,
      focusable: true,
      show: false,
      autoHideMenuBar: true,
      backgroundColor: '#282c34',
      webPreferences: {
        preload: path.join(__dirname, 'preload.js'),
        webSecurity: false
      }
    });

    mainWindow.loadFile(path.join(appDir, 'wallpaper.html'));

    mainWindow.once('ready-to-show', () => {
      mainWindow.setBounds(bounds);
      mainWindow.showInactive();

      setTimeout(() => {
        const hwndBuffer = mainWindow.getNativeWindowHandle();
        let hwnd;
        try {
          hwnd = hwndBuffer.readBigUInt64LE(0).toString();
        } catch (_) {
          hwnd = hwndBuffer.readUInt32LE(0).toString();
        }
        embedToDesktop(hwnd, bounds, PARENT_MODE);

        setTimeout(() => {
          if (mainWindow && !mainWindow.isDestroyed()) {
            mainWindow.setBounds(bounds);
            mainWindow.showInactive();
          }
        }, 200);
      }, 300);
    });

    mainWindow.on('closed', () => { mainWindow = null; });

    console.log('Mode: desktop embed (parent=' + PARENT_MODE + ')');
  }
}

// ── Verify required files ─────────────────────────────────

function printStartupInfo() {
  console.log('[app] version 0.2.0');
  console.log('[app] packaged:', isPackaged);
  console.log('[app] resourcesDir:', resourcesDir);
  console.log('[app] appDir:', appDir);
  console.log('[app] helper:', fs.existsSync(helperExe) ? 'found' : 'missing');
  console.log('[app] desktop_host:', fs.existsSync(desktopHostExe) ? 'found' : 'missing');

  const killBat = path.join(__dirname, 'Kill CMD Wallpaper.bat');
  if (fs.existsSync(killBat)) {
    console.log('[app] Kill script:', killBat);
  } else {
    console.log('[app] Kill script not bundled. Run: taskkill /F /IM "CMD Wallpaper Desktop.exe"');
  }
}

// ── App lifecycle ─────────────────────────────────────────

app.whenReady().then(() => {
  printStartupInfo();
  startHelper();
  createTray();
  registerShortcuts();
  createWindow();
});

app.on('window-all-closed', () => {
  forceQuit();
});

app.on('will-quit', () => {
  globalShortcut.unregisterAll();
});

app.on('before-quit', () => {
  stopDesktopHost();
  stopHelper();
});

app.on('activate', () => {
  // macOS dock click guard — no-op
});
