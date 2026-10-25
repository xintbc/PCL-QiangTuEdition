# PCL 枪战突击修改版（PCL QiangTu Edition）

> **本软件是第三方基于 [Plain Craft Launcher 2 (PCL)](https://github.com/Meloong-Git/PCL) 独立进行二次创作的产物，与 PCL 官方无关。**
>
> 原项目作者：**龙腾猫跃** —— 请支持原作者：<https://afdian.net/@LTCat>

把 PCL 改造为《枪战突击》（Three.js 网页 3D 枪战游戏）的专用启动器：
版本列表 / 多线程下载 / 独立版本目录 / 模组下载，全部对接
[枪战突击发布中心](https://xintbc.github.io/qiangtuskill/)。

## 和原版 PCL 的区别

| 项 | 原版 PCL | 本修改版 |
|---|---|---|
| 版本来源 | Minecraft 版本 JSON | 枪战突击 `manifest.json` |
| 下载对象 | MC 客户端 / 资源 / Mod | 游戏版本 zip / 模组 js |
| 启动方式 | 调用 Java | 打开对应版本的 `index.html` |
| 模组 | MC Mod（jar） | 枪战突击模组（js） |

保留了 PCL 的：界面框架、主题、多线程下载器、文件工具、动画系统。

## 构建

本机不编译，走 GitHub Actions（`.github/workflows/build.yml`）：

1. push 到 `main`
2. Actions 自动跑 `msbuild`（.NET Framework 4.8 + VB.NET + C#）
3. 在 Actions 页面下载 `PCL-QiangTuEdition` 产物

## 许可证

遵循原项目的 [LICENCE](LICENCE)（《PCL 分发有限许可》+《存储库合理使用指南》）。
本修改版属于「重度使用」，已按指南要求：

1. 明确表示是第三方基于 PCL 独立二次创作的产物
2. 名称以 `PCL` 开头并带第三方修改后缀（枪战突击修改版）
3. 关于页署名龙腾猫跃 + 赞助链接，且公开源代码
4. Minecraft 启动功能将移除，故不涉及正版劝导（McLaunchPrecheck）
5. 界面主色相保持蓝色，无赞助解锁相关功能

## 由 AI 创作

本修改版的改造代码由 AI 生成；原 PCL 代码版权归龙腾猫跃所有。
