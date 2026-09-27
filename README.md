# DesktopNest

> **本仓库为 DesktopNest 唯一官方仓库**（github.com/Nico-ko-ko/DesktopNest）。任何其他网站、网盘、论坛或个人主页分发的副本均非官方，请以本仓库为准。

DesktopNest 是一个使用 C# 和 WinForms 编写的 Windows 桌面软件收纳工具，最多可收纳 100 个项目，容量可在 4–100 之间自由调节（点标题栏书包按钮即可）。桌面项目会移动进收纳袋，移出时放回原桌面位置；除用户主动使用图片链接下载背景外，程序不访问网络，也不依赖第三方框架或额外运行时安装。

## 运行

直接双击：

```text
dist\DesktopNest.exe
```

首次运行后，数据保存在：

```text
%LOCALAPPDATA%\DesktopNest
```

## 使用

1. 从桌面把一个或多个快捷方式拖进窗口。
2. 双击图标启动对应软件。
3. 右键图标可打开、重命名或移出收纳。
4. 右键窗口空白处可以从文件添加、一键移出全部项目，或打开数据目录。
5. 双击窗口标题名称可修改工具名称。
6. 调整窗口位置和大小后关闭，下次启动会自动恢复。
7. 在任务栏右键 DesktopNest 图标，选择“固定到任务栏”。
8. 点击标题栏右上角、最小化按钮左侧的无底色齿轮，可设置纯色、线性渐变或背景图片。
9. 背景图片支持本地上传、图片链接和当前桌面壁纸；GIF/APNG/动态 WebP 会循环播放。

默认窗口为 `640 × 720`，使用紧凑的 8 × 8 网格；在屏幕高度允许时会自动展开窗口，一次显示全部（最多 100 个）项目。屏幕空间不足时仍可滚动查看。

从桌面拖入后，原桌面项目会立即消失并保存在 DesktopNest 的数据目录中。选择“移出收纳”后，项目会移动回原来的桌面位置；空白处右键选择“一键移出”时，收纳袋中的所有项目都会回到桌面：原桌面项目按原名恢复，其他来源项目会在桌面生成不重名图标。失败的项目会保留在收纳袋中并汇总提示。

## 构建

本机不需要安装 .NET SDK 或第三方包。Windows 自带的 .NET Framework 编译器即可构建：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

构建脚本会生成程序图标、输出 `dist\DesktopNest.exe`，并自动执行自检。

只构建、不执行自检：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SkipSelfTest
```

## 自检

```powershell
.\dist\DesktopNest.exe --self-test
```

成功返回退出代码 `0`，失败返回非零退出代码并输出失败原因。

## 数据

```text
%LOCALAPPDATA%\DesktopNest\
  data.json
  items\
    <唯一标识>.<原扩展名>
    <唯一标识>\
```

`data.json` 使用临时文件加原子替换方式写入，保存工具名称、窗口位置、窗口尺寸、最大化状态、原桌面路径和收纳顺序。

背景外观和图片缓存保存在程序运行目录：

```text
config\background.json
config\bg_cache\
```

访问过的图片链接会缓存在 `bg_cache`，之后断网启动仍可使用上次图片。
