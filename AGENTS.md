# Javideo — Agent 开发守则(采纳 Ponytail 方法论)

> Lazy, not negligent. 最好的代码是你从未写过的代码。

在为这个项目写任何代码前,先爬这个决策梯,**停在第一个成立的横档上**:

1. **这东西真的需要存在吗?** → 不需要就跳过(YAGNI)。不存在的功能没有 bug、没有 CVE、永远 100% 可用。
2. **标准库能做吗?** → 用标准库。
3. **平台原生功能能做吗?** → 用原生(.NET / Vue / Tauri 自带能力,不引第三方)。
4. **已安装的依赖能做吗?** → 复用它,不新增依赖。
5. **能一行写完吗?** → 就一行。
6. **以上都不行,才写「能工作的最少代码」。**

## 永不偷懒的红线
懒,不是偷工减料。以下**永远不省**:
- **信任边界校验**(外部输入、API 边界、用户数据)
- **数据丢失处理**(删除/覆盖前确认、写文件失败回滚)
- **安全**(注入、路径穿越、ACL、密钥)
- **可访问性 / 国际化**

## 执行准则
- **能复用就复用**:项目里已有 `MovieDetailDrawer`、`favorites` store、`confirmDialog` 等,新功能先找现成的用,不要重造。
- **能少引库就少引**:别为了一个小功能装一个包。优先用 .NET / Vue / Tauri / UnoCSS 原生能力。
- **能删就删**:发现的冗余、重复、过度抽象,直接删掉。代码越少越好维护。
- **每个偷的懒都标记**:`ponytail:` 注释标出"如果以后要升级,路径在这里",方便回填。

## 已知的复用资产(优先用这些)
| 需求 | 用这个 |
|---|---|
| 影片详情弹窗 | `MovieDetailDrawer.vue` |
| 收藏状态 | `stores/favorites.ts`(`ensureLoaded`/`isFav`/`toggle`) |
| 确认对话框 | `utils/confirm.ts`(`confirmDialog`) |
| 弹框背景关闭 | `utils/clickOutside.ts`(`useBackdropClose`) |
| 主题切换 | `utils/theme.ts` |
| 文件夹选择 | `utils/pickFolder.ts` |
| 连 worker | `api/worker.ts`(`getBaseUrl`) |
| 设置存取 | `stores/settings.ts` |

写新代码前,先查这张表。

## 构建交付物
最终产物都放在 `Javideo-app\` 下。

### 完整构建流程
1. **编译 .NET Worker 侧边栏**
   ```
   dotnet publish worker -c Release -r win-x64 --self-contained false
   ```
2. **替换侧边栏二进制**（Tauri 构建时会内嵌此文件）
   ```
   Copy-Item worker\bin\Release\net8.0\win-x64\publish\javideo-worker.exe `
     src-tauri\binaries\javideo-worker-x86_64-pc-windows-msvc.exe -Force
   ```
3. **构建 Tauri 桌面应用**
   ```
   & "$env:COMSPEC" /c "npm run tauri build"
   ```
   （会自动先跑 `npm run build`，NSIS 安装包下载超时不影响 exe）
4. **复制交付物到 Javideo-app\**
   ```
   Copy-Item src-tauri\target\release\javideo.exe Javideo-app\javideo.exe -Force
   Copy-Item worker\bin\Release\net8.0\win-x64\publish\javideo-worker.exe Javideo-app\javideo-worker.exe -Force
   ```
