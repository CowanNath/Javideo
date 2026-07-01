export interface Library {
  id: number
  name: string
  metadataSource: string
  directories: string[]
  movieCount?: number
}

export interface Actor {
  id: number
  name: string
  avatarUrl?: string | null
  movieCount?: number
}

export interface ActorDetail {
  name: string
  avatarUrl?: string | null
  summary?: string | null
  birthday?: string | null
  height?: number | null
  measurements?: string | null
  cupSize?: string | null
  bloodType?: string | null
  hobby?: string | null
  skill?: string | null
  nationality?: string | null
  aliases?: string[]
  images?: string[]
  homepage?: string | null
  provider?: string | null
}

export interface ActorDetailResponse {
  actor: ActorDetail | null
  name: string
  movies: Movie[]
  configError?: string | null
}

export interface Tag {
  id: number
  name: string
  category: 'genre' | 'series' | 'maker' | 'custom' | string
  isStandard: boolean
  movieCount?: number
}

export interface MagnetResult {
  title: string
  size: string
  magnetUri: string
  source: string
}

export interface MagnetSourceResult {
  source: string
  count: number
  results: MagnetResult[]
}

export interface Movie {
  id?: number
  libraryId?: number | null
  number: string
  title?: string | null
  originalTitle?: string | null
  summary?: string | null
  maker?: string | null
  label?: string | null
  series?: string | null
  director?: string | null
  releaseDate?: string | null
  runtimeMinutes?: number | null
  coverUrl?: string | null
  thumbUrl?: string | null
  score?: number | null
  provider?: string | null
  homepageUrl?: string | null
  folderPath?: string | null
  actors?: Actor[]
  tags?: Tag[]
  magnets?: MagnetResult[]
  previewImages?: string[]
  hasTrailer?: boolean
}

export type FavoriteTarget = 'movie' | 'tag' | 'actor'

export interface Favorite {
  id: number
  targetType: FavoriteTarget
  targetId: number
  name?: string | null
  subtitle?: string | null
  cover?: string | null
}

export interface IngestResult {
  movieId: number
  folderPath: string | null
  imagesOk: boolean
}

export interface ScanResult {
  availableDirs: number
  skippedDirs: number
  files: { number: string; filePath: string; fileName: string }[]
  logs: string[]
}
