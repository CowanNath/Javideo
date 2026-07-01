# Javideo

本地番号刮削与媒体库管理工具(Windows 桌面软件)。

## 功能概览
- **搜索**:按番号/演员搜索,展示刮削内容(MetaTube)和磁力链接。
- **收藏**:影片 / 标签 / 演员三类入口,集中管理常看内容。
- **演员**:浏览演员及其作品。
- **标签**:按 `类型 / 系列 / 厂商 / 自定义` 浏览,支持标准库与非标准库。
- **设置**:媒体库管理、MetaTube、播放器、诊断、关于。
- **媒体库**:在指定目录下生成 `{番号}/`,内含 `.nfo`、`poster.jpg`、`thumb.jpg`、`磁力链接.txt`。

## 架构(两层)
- **前端层**:Tauri 桌面壳 + Vue3/TS/Pinia/UnoCSS,负责页面、设置、播放器交互。
- **Worker 层**:.NET 8 后端(Kestrel HTTP),负责扫描目录、请求 MetaTube、维护 SQLite、生成缓存、写入 nfo/图片/磁力.txt。

### 进程通信
Worker 以 Tauri **sidecar** 方式启动,监听 `127.0.0.1:0`(系统分配端口),启动后在 stdout 打印:
```
JAVIDEO_WORKER_PORT=12345
```
Rust 壳读取该行,通过 Tauri 命令 `get_worker_url` 暴露给前端,前端用 `fetch` 直连。

## 目录结构
```
Javideo/
├─ src/            前端(Vue3)
├─ src-tauri/      Tauri 壳(Rust)
│  ├─ src/lib.rs   启动 sidecar、读端口、暴露命令
│  ├─ capabilities/ sidecar 执行权限
│  └─ binaries/    发布后的 worker exe(sidecar)
├─ worker/         .NET 8 Worker
│  ├─ Program.cs   Kestrel + 端口握手
│  ├─ Db/          SQLite schema + 连接工厂
│  ├─ Services/    MetaTube/扫描/入库/NFO/图片
│  ├─ Magnet/      磁力源(PollackSource + 接口)
│  ├─ Endpoints/   /api/* 路由
│  └─ Models/      DTO
└─ scripts/        构建 worker sidecar 的脚本
```

## 开发

### 前置依赖
- Node.js 18+、npm
- .NET 8 SDK
- Rust(stable)+ **MSVC 构建工具**(Visual Studio Build Tools,勾选「使用 C++ 的桌面开发」)
- Windows SDK

> ⚠️ 编译 Rust 壳时,必须让 MSVC 的 `link.exe` 在 PATH 里优先于 Git Bash 自带的 GNU `link`,否则链接会报 `extra operand`。本项目提供了 `scripts/cargo-msvc.bat` 自动加载 MSVC 环境后再跑 cargo。

### Cargo 国内镜像(可选,加速依赖下载)
`~/.cargo/config.toml` 已配置 rsproxy.cn 镜像。如需换源,编辑该文件。

### 1. 构建 Worker sidecar(.NET 改动后需重跑)
```bash
bash scripts/build-worker.sh
```
产物放到 `src-tauri/binaries/javideo-worker-x86_64-pc-windows-msvc.exe`,供 `tauri build` 打包时拾取。

### 2. 打包成可双击运行的桌面应用(推荐方式)
```cmd
scripts\build-worker.sh            (Git Bash 里跑:重新生成 worker)
scripts\tauri-build.bat --no-bundle  (产出 src-tauri\target\release\javideo.exe)
bash scripts\make-portable.sh        (组装 Javideo-app\ 可运行目录)
```
然后双击 `Javideo-app\javideo.exe` 即可。它会自动拉起同目录的 `javideo-worker.exe`。

> ⚠️ **关键经验**:
> - `tauri build`(而非裸 `cargo build`)才会把前端 dist 嵌入 exe;裸 `cargo build --release` 出的 exe 会白屏。
> - `Cargo.toml` 里 `tauri` 必须带 `custom-protocol` feature,否则 release 也按 dev 模式加载(devUrl)→ 白屏。
> - sidecar 运行时按 `<exe目录>/javideo-worker.exe` 查找(不看 `binaries/` 子目录、不看 target-triple 后缀)。`tauri build` 会自动把 sidecar 复制到 exe 同级;手动部署见 `make-portable.sh`。

### 3. 开发热更新(tauri dev,vite dev server + sourcemap)
```cmd
scripts\tauri-dev.bat
```
会自动起 vite dev server(localhost:1420)+ 编译 Rust 壳 + spawn worker sidecar。改前端代码实时刷新。

### 4. 纯前端 + 单独 Worker(浏览器联调,无需 Tauri)
终端 A(Worker):
```bash
cd worker && dotnet run
# 看到 JAVIDEO_WORKER_PORT=1375 即可(开发固定端口)
```
终端 B(前端):
```bash
npm install
npm run dev      # 浏览器开 http://localhost:1420
```
> 此模式下前端连 `http://127.0.0.1:1375`(可用 `VITE_DEV_WORKER_PORT` 覆盖)。


## 数据来源
- **刮削**:[MetaTube](https://github.com/metatube-community/metatube-sdk-go)(用户在设置页自填服务器地址)。
- **磁力**:pollack3.sbs(HTML 解析)。yhg007 / btdig 接口已留,后续接入。

## 合规边界
本软件为本地媒体库管理工具,不内置任何下载器;磁力链接仅以文本形式展示并写入 txt,由用户自行用外部工具处理,不涉及下载或分发。
