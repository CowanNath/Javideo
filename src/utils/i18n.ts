// Simple i18n — no library, just a dictionary + reactive current language.
// Ponytail: standard object lookup, no vue-i18n dependency.
import { ref } from 'vue'
import { getStoredTheme } from '@/utils/theme'

export type Lang = 'zh' | 'en'

const dict = {
  zh: {
    // sidebar
    search: '搜索', favorites: '收藏', actors: '演员', tags: '标签', settings: '设置',
    libraries: '媒体库', noLibraries: '暂无媒体库\n去「设置」新建一个',
    workerRunning: 'Worker 运行中', collapse: '收起',
    // search
    searchPlaceholder: '例如：SSIS-001',
    searchBtn: '搜索', searching: '搜索中…',
    stepScrape: '元数据', stepMagnet: '磁力链接', stepTrailer: '预告片', stepTranslate: '翻译',
    openFolder: '打开文件夹',
    searchSubtitle: '输入番号，刮削元数据并搜索磁力链接',
    number: '番号', actorsLabel: '演员',
    selectLib: '选择目标媒体库', ingest: '入库', ingesting: '入库中…',
    notIngestedHint: '先到「设置 → 媒体库」新建一个媒体库。',
    selectLibWarn: '⚠ 请先选择目标媒体库',
    needsConfig: '未配置 MetaTube 服务地址，请到「设置 → MetaTube」填写',
    goConfig: '去配置',
    magnetLinks: '磁力链接',
    ingested: '已入库',
    // favorites
    favoritesSubtitle: '集中管理常看内容',
    movie: '影片', noFavs: '暂无收藏的',
    searchNameOrId: '搜索名称或 ID',
    recent: '最近收藏', byId: '按 ID', selectAll: '全选',
    batchUnfav: '批量取消', unfavConfirm: '取消收藏', unfav: '取消收藏',
    // actors
    actorsSubtitle: '点击演员查看其信息与作品',
    searchActor: '搜索演员名', noActors: '暂无演员，刮削入库后会出现在这里',
    videos: 'videos', backToActors: '返回演员',
    relatedWorks: '相关作品', noWorks: '该演员暂无已入库的作品',
    // tags
    genre: '类型', series: '系列', maker: '厂商', custom: '自定义',
    noTags: '暂无标签', backToTags: '返回标签', tagMovies: '标签影片',
    noTagMovies: '该标签暂无影片',
    // library
    scanDir: '扫描目录', scanning: '扫描中…', loading: '加载中…',
    noMovies: '该库还没有影片', noMoviesHint: '去「搜索」页刮削并入库，或点击右上角扫描目录',
    availDirs: '可用目录', skipped: '跳过', scannedFiles: '识别文件',
    ingestAll: '全部入库', ingestedTag: '已入库',
    // detail drawer
    play: '播放', starting: '启动中…', rescrape: '重新刮削', scraping: '正在刮削…',
    scraped: '已重新刮削', rescrapeFail: '重刮失败', delete: '删除',
    deleteConfirm: '删除该影片记录？(连同生成的文件夹一并删除)',
    deleteLibConfirm: '删除该媒体库？(库内影片记录会保留，但失去关联。)',
    deleteMovie: '删除影片', deleteLib: '删除媒体库',
    // settings
    settingsSubtitle: '配置媒体库、刮削、播放与外观',
    newLib: '新建', libName: '媒体库名称', metaSource: '元数据来源',
    useMetatube: '使用 MetaTube 搜刮', dirs: '目录', addDir: '追加目录',
    noLibsHint: '还没有媒体库，点右上角「新建」。',
    metatube: 'MetaTube', metatubeDesc: '服务地址、请求超时、连通性测试',
    serverAddr: '服务地址', timeoutMs: '请求超时 (毫秒)',
    testConn: '连通性测试', testing: '测试中…', save: '保存',
    basic: '基本', basicDesc: '语言、主题、刮削、关闭行为、调试模式',
    language: '语言', theme: '主题', dark: '深色', light: '浅色',
    scrapeTrailer: '刮削预告片(trailer)', scrapeTrailerHint: '入库时从 DMM 下载预告片到影片文件夹',
    enabled: '已开启', disabled: '已关闭',
    closeBehavior: '关闭行为', closeQuit: '直接退出', closeTray: '最小化到托盘',
    defaultSort: '默认排序', sortByDate: '按添加时间', sortByName: '按名称', sortByCount: '按作品数',
    debugMode: '调试模式', debugOn: '调试模式已开启', debugOff: '调试模式已关闭',
    player: '播放器设置', playerDesc: '自定义播放器路径，未配置时回退系统默认',
    playerPath: '播放器路径',
    networkProxy: '网络代理', proxyDesc: '用于下载 DMM 预告片(预告片服务器对非日本 IP 返回 403)',
    proxyAddr: '代理地址', proxyUser: '用户名(可选)', proxyPass: '密码(可选)',
    noAuth: '无认证则留空', noProxyHint: '留空=不使用代理',
    about: '关于', aboutDesc: '版本 / 更新',
    importExport: '用户数据导入导出', importExportDesc: '迁移、备份、恢复用户环境(开发中)',
    devInProgress: '该功能尚在开发中。',
    exportData: '导出备份', importData: '导入备份',
    exportDone: '备份已导出', exportFail: '导出失败',
    exporting: '正在导出备份…',
    importConfirm: '导入会覆盖当前数据,确定继续?',
    importing: '正在导入…', importFail: '导入失败',
    saved: '设置已保存', langSaved: '语言已设为中文(刷新后生效)',
    trailerOn: '已开启预告片刮削', trailerOff: '已关闭预告片刮削',
    proxySaved: '代理设置已保存', playerSaved: '播放器设置已保存',
    metatubeSaved: 'MetaTube 设置已保存',
    langEnNote: 'Language set to English (reload to apply)',
    newLibTitle: '新建媒体库', editLibTitle: '编辑媒体库',
    cancel: '取消', saveLib: '保存媒体库',
    nameTaken: '该名称已被使用，请更改', nameTakenHint: '该名称已被使用',
    dirNotExist: '目录不存在', dirValid: '目录有效',
    dirHint: '目录不存在时无法保存。标准库扫描识别规范命名的 mp4 / mkv / strm 文件。',
    browseFolder: '选择文件夹', remove: '移除',
    // detail drawer extras
    noPreviewImg: '无封面',
    // library extras
    movies: '部影片', part: '部', searchNameOrId2: '搜索名称',
    // scan
    notRecognized: '未识别', scanFailed: '扫描失败',
    // shared extras (i18n expansion)
    editTag: '编辑标签', editTagName: '编辑标签名称',
    tagSubtitle: '数字表示该标签下的影片数量',
    tagUpdated: '标签已更新', favTag: '收藏标签',
    ingestingScrape: '刮削入库中…', loadFailed: '加载失败',
    deleteFailed: '删除失败', tagAdded: '标签已添加',
    addCustomTag: '添加自定义标签', tagName: '标签名',
    enterLibName: '请填写媒体库名称',
    fixDirs: '存在不正确的目录,请修正后再保存(不存在的目录需创建或重新选择)',
    libNamePlaceholder: '例如:我的影片库', dirPlaceholder: '例如:D:\\Videos\\Movies',
    // common
    confirm: '确认', minutes: '分钟',
    // search page
    searchTitle: '搜索', multipleResults: '找到多个结果,请选择',
    // favorites extras
    unfavItem: '取消收藏', removed: '已移除',
    // actors extras
    actorDetail: '演员资料',
    // tags extras
    tagTitle: '标签',
    // library edit dialog extras
    dirNotExistShort: '不存在', dirValidShort: '有效',
    noPlayer: '未配置播放器,使用系统默认',
    playFailed: '播放失败',
    // about
    version: '版本', update: '更新',
  },
  en: {
    search: 'Search', favorites: 'Favorites', actors: 'Actors', tags: 'Tags', settings: 'Settings',
    libraries: 'Libraries', noLibraries: 'No library yet\nCreate one in Settings',
    workerRunning: 'Worker running', collapse: 'Collapse',
    searchPlaceholder: 'e.g. SSIS-001',
    searchBtn: 'Search', searching: 'Searching…',
    stepScrape: 'Metadata', stepMagnet: 'Magnet', stepTrailer: 'Trailer', stepTranslate: 'Translate',
    openFolder: 'Open Folder',
    searchSubtitle: 'Enter a code or actor name to scrape metadata and search magnets',
    number: 'Code', actorsLabel: 'Actors',
    selectLib: 'Select library', ingest: 'Ingest', ingesting: 'Ingesting…',
    notIngestedHint: 'Create a library first in Settings → Libraries.',
    selectLibWarn: '⚠ Please select a target library first',
    needsConfig: 'MetaTube address not configured — go to Settings → MetaTube',
    goConfig: 'Configure',
    magnetLinks: 'Magnet Links',
    ingested: 'In Library',
    favoritesSubtitle: 'Manage your favorite content',
    movie: 'Movies', noFavs: 'No favorites yet',
    searchNameOrId: 'Search name or ID',
    recent: 'Recent', byId: 'By ID', selectAll: 'Select all',
    batchUnfav: 'Unfavorite', unfavConfirm: 'Remove favorite', unfav: 'Unfavorite',
    actorsSubtitle: 'Click an actor to view info and works',
    searchActor: 'Search actor name', noActors: 'No actors yet — they appear after scraping',
    videos: 'videos', backToActors: 'Back to Actors',
    relatedWorks: 'Related Works', noWorks: 'No ingested works for this actor yet',
    genre: 'Genre', series: 'Series', maker: 'Maker', custom: 'Custom',
    noTags: 'No tags yet', backToTags: 'Back to Tags', tagMovies: 'Tag Movies',
    noTagMovies: 'No movies for this tag yet',
    scanDir: 'Scan', scanning: 'Scanning…', loading: 'Loading…',
    noMovies: 'No movies in this library yet',
    noMoviesHint: 'Scrape & ingest from Search, or scan the directory',
    availDirs: 'Available', skipped: 'Skipped', scannedFiles: 'Files found',
    ingestAll: 'Ingest All', ingestedTag: 'Ingested',
    play: 'Play', starting: 'Starting…', rescrape: 'Re-scrape', scraping: 'Scraping…',
    scraped: 'Re-scraped', rescrapeFail: 'Re-scrape failed', delete: 'Delete',
    deleteConfirm: 'Delete this movie record? (Its generated folder will be removed too.)',
    deleteLibConfirm: 'Delete this library? (Movie records remain, but lose association.)',
    deleteMovie: 'Delete Movie', deleteLib: 'Delete Library',
    settingsSubtitle: 'Configure libraries, scraping, player and appearance',
    newLib: 'New', libName: 'Library name', metaSource: 'Metadata source',
    useMetatube: 'Use MetaTube', dirs: 'Directories', addDir: 'Add directory',
    noLibsHint: 'No library yet — click "New" above.',
    metatube: 'MetaTube', metatubeDesc: 'Server address, timeout, connectivity test',
    serverAddr: 'Server address', timeoutMs: 'Timeout (ms)',
    testConn: 'Test', testing: 'Testing…', save: 'Save',
    basic: 'Basic', basicDesc: 'Language, theme, scraping, close behavior, debug',
    language: 'Language', theme: 'Theme', dark: 'Dark', light: 'Light',
    scrapeTrailer: 'Scrape trailer', scrapeTrailerHint: 'Download trailer from DMM on ingest',
    enabled: 'Enabled', disabled: 'Disabled',
    closeBehavior: 'Close Behavior', closeQuit: 'Quit', closeTray: 'Minimize to Tray',
    defaultSort: 'Default Sort', sortByDate: 'By Date Added', sortByName: 'By Name', sortByCount: 'By Movie Count',
    debugMode: 'Debug Mode', debugOn: 'Debug mode enabled', debugOff: 'Debug mode disabled',
    player: 'Player', playerDesc: 'Custom player path; falls back to system default',
    playerPath: 'Player path',
    networkProxy: 'Network Proxy', proxyDesc: 'For downloading DMM trailers (403 for non-JP IPs)',
    proxyAddr: 'Proxy address', proxyUser: 'Username (optional)', proxyPass: 'Password (optional)',
    noAuth: 'Leave empty if no auth', noProxyHint: 'Empty = no proxy',
    about: 'About', aboutDesc: 'Version / Updates',
    importExport: 'Import / Export', importExportDesc: 'Migrate, backup, restore (in development)',
    devInProgress: 'This feature is in development.',
    exportData: 'Export Backup', importData: 'Import Backup',
    exportDone: 'Backup exported', exportFail: 'Export failed',
    exporting: 'Exporting backup…',
    importConfirm: 'Import will overwrite current data. Continue?',
    importing: 'Importing…', importFail: 'Import failed',
    saved: 'Settings saved', langSaved: 'Language set to Chinese (reload to apply)',
    trailerOn: 'Trailer scraping enabled', trailerOff: 'Trailer scraping disabled',
    proxySaved: 'Proxy settings saved', playerSaved: 'Player settings saved',
    metatubeSaved: 'MetaTube settings saved',
    langEnNote: 'Language set to English (reload to apply)',
    newLibTitle: 'New Library', editLibTitle: 'Edit Library',
    cancel: 'Cancel', saveLib: 'Save Library',
    nameTaken: 'Name already taken — please change it', nameTakenHint: 'Name already taken',
    dirNotExist: 'Directory does not exist', dirValid: 'Directory valid',
    dirHint: 'Directories must exist to save. Standard scan recognizes mp4 / mkv / strm.',
    browseFolder: 'Browse', remove: 'Remove',
    noPreviewImg: 'No cover',
    movies: 'movies', part: '',
    searchNameOrId2: 'Search name',
    notRecognized: 'Not recognized', scanFailed: 'Scan failed',
    // shared extras (i18n expansion)
    editTag: 'Edit tag', editTagName: 'Edit tag name',
    tagSubtitle: 'Numbers show the movie count per tag',
    tagUpdated: 'Tag updated', favTag: 'Favorite tag',
    ingestingScrape: 'Scraping & ingesting…', loadFailed: 'Load failed',
    deleteFailed: 'Delete failed', tagAdded: 'Tag added',
    addCustomTag: 'Add custom tag', tagName: 'Tag name',
    enterLibName: 'Please enter a library name',
    fixDirs: 'Some directories are invalid. Fix them before saving (missing dirs must be created or re-selected).',
    libNamePlaceholder: 'e.g. My Movies', dirPlaceholder: 'e.g. D:\\Videos\\Movies',
    confirm: 'OK', minutes: 'min',
    searchTitle: 'Search', multipleResults: 'Multiple results found, pick one',
    unfavItem: 'Unfavorite', removed: 'Removed',
    actorDetail: 'Actor Profile',
    tagTitle: 'Tags',
    dirNotExistShort: 'Missing', dirValidShort: 'Valid',
    noPlayer: 'No player configured, using system default',
    playFailed: 'Playback failed',
    version: 'Version', update: 'Update',
  }
} as const

export type TKey = keyof typeof dict.zh

export const currentLang = ref<Lang>('zh')

export function initLang() {
  const stored = localStorage.getItem('javideo.language')
  currentLang.value = stored === 'en' ? 'en' : 'zh'
  document.documentElement.lang = currentLang.value === 'en' ? 'en' : 'zh-CN'
}

export function setLang(lang: Lang) {
  currentLang.value = lang
  localStorage.setItem('javideo.language', lang)
  document.documentElement.lang = lang === 'en' ? 'en' : 'zh-CN'
}

export function t(key: TKey): string {
  return dict[currentLang.value][key] ?? dict.zh[key] ?? key
}
