# LyricBar - Windows 桌面歌词条

通过 Windows SMTC 系统媒体接口适配所有主流播放器（网易云 / QQ音乐 / Spotify / 浏览器等），
在桌面上显示一条任务栏风格的歌词条。

## 使用
1. 双击 `publish\LyricBar.exe` 启动（需要 .NET 8 Desktop Runtime，本机已装 SDK）。
2. 在任意播放器中播放音乐，歌条会显示歌名并同步滚动歌词。
3. 鼠标悬停歌条，出现 上一首 / 播放暂停 / 下一首 控制按钮。
4. 按住歌条拖动，松手后自动吸附到最近的屏幕边缘（上下左右均可）。
5. 托盘图标：双击打开设置；右键菜单可退出。

## 设置项
- 字体大小（12-32）
- 歌词偏移（毫秒，正数提前、负数延后）
- 鼠标点击穿透
- 开机自动启动

## 数据位置
- 设置：`%LocalAppData%\LyricBar\settings.json`
- 歌词缓存：`%LocalAppData%\LyricBar\cache\`

## 歌词来源
网易云歌词接口优先，失败回退 QQ 音乐接口，均失败时使用本地缓存。

## 开发
- 构建：`dotnet build LyricBar.sln`
- 测试：`dotnet test LyricBar.sln`
- 发布单文件：`dotnet publish src/LyricBar -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`

注意：本仓库的 `nuget.config` 配置了国内镜像源（腾讯云/华为云），
直连 nuget.org 不稳定的网络环境下可正常还原依赖。
