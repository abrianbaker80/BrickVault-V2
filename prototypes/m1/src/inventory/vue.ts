import { createApp } from 'vue'
import Inventory from './Inventory.vue'
import { createRegister } from './model'
import './inventory.css'

export function mountInventory(host: HTMLElement) {
  const api = createRegister(host.clientWidth)
  const app = createApp(Inventory, { api, host })
  app.mount(host)
  return () => {
    app.unmount()
    api.dispose()
  }
}
