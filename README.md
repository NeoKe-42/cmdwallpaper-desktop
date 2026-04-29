# CMD Wallpaper Desktop

这是 CMD Wallpaper 的非 Wallpaper Engine 实验版本。

## 运行方法

开发运行：

```
npm install
npm run build:host
npm start
```

普通窗口调试：

```
npm run start:windowed
```

## 打包

```
npm run pack                # 生成未压缩目录（调试用）
npm run dist:portable       # 生成 portable exe
npm run dist:nsis           # 生成安装包
```

打包输出在 `dist/` 目录。

## Desktop mode

Desktop mode 将 Electron 窗口嵌入 Windows 桌面层，使其成为真正的桌面背景。

默认使用 **Progman** 模式（Windows 11 下更稳定）。

- 无边框，不出现在任务栏
- 窗口嵌入桌面层
- 普通应用窗口在其上方
- 不阻挡鼠标操作
- 托盘右键退出
- 快捷键：`Ctrl+Alt+Q` 退出，`Ctrl+Alt+R` 刷新

运行模式：

```
npm start                   # 默认 Progman 桌面嵌入
npm run start:progman       # 明确 Progman 模式
npm run start:workerw       # WorkerW 模式（实验性）
npm run start:windowed      # 普通窗口调试模式
```

## 依赖

- Node.js
- .NET 9.0 运行时 / SDK（helper 和 desktop_host 需要）

原项目路径：
F:\1123\cmdwallpaper
