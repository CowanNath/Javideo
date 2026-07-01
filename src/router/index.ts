import { createRouter, createWebHashHistory } from 'vue-router'

const routes = [
  { path: '/', redirect: '/search' },
  { path: '/search', name: 'search', component: () => import('@/views/SearchView.vue') },
  { path: '/favorites', name: 'favorites', component: () => import('@/views/FavoritesView.vue') },
  { path: '/actors', name: 'actors', component: () => import('@/views/ActorsView.vue') },
  { path: '/actors/:id', name: 'actor-detail', component: () => import('@/views/ActorDetailView.vue') },
  { path: '/tags', name: 'tags', component: () => import('@/views/TagsView.vue') },
  { path: '/tags/:id', name: 'tag-detail', component: () => import('@/views/TagDetailView.vue') },
  { path: '/settings', name: 'settings', component: () => import('@/views/SettingsView.vue') },
  { path: '/library/:id', name: 'library', component: () => import('@/views/LibraryView.vue') },
]

export const router = createRouter({
  history: createWebHashHistory(),
  routes,
})
