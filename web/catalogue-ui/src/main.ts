import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { router } from './router'
import { applyTheme, theme } from './ui'
import { restoreSession } from './api'
import './style.css'

applyTheme(theme.value)

// Restore the session from the httpOnly refresh cookie before the router's first auth check.
restoreSession().finally(() => createApp(App).use(createPinia()).use(router).mount('#app'))
