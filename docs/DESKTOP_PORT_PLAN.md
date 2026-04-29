# Desktop Port Plan

当前目标是探索一个不依赖 Wallpaper Engine 的版本。

原 Wallpaper Engine 版本依赖以下接口：

- wallpaperRegisterAudioListener
- wallpaperRegisterMediaPropertiesListener
- wallpaperRegisterMediaThumbnailListener
- wallpaperRegisterMediaTimelineListener

这些接口在普通浏览器或普通桌面应用中不可用。

桌面版需要重新实现：

- 媒体标题
- 歌手
- 专辑封面
- 播放进度
- EQ 音频响应
- 桌面置底显示

第一阶段目标：

- 复用当前 UI
- 显示系统信息
- 暂时禁用 Wallpaper Engine 专属接口
- 先做一个可打开的桌面窗口或本地 HTML 预览
