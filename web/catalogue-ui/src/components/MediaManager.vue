<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Icon from './Icon.vue'
import { api, errorText, type Asset } from '../api'
import { fmt, toast } from '../ui'

const props = defineProps<{ productId: number }>()
const assets = ref<Asset[]>([])
const over = ref(false)
const kind = ref('')
const uploading = ref(false)
const confirmDelete = ref<number | null>(null)
const input = ref<HTMLInputElement>()

const images = computed(() => assets.value.filter(a => a.kind === 'Image'))
const documents = computed(() => assets.value.filter(a => a.kind !== 'Image'))

async function load() {
  assets.value = (await api.get<Asset[]>(`/products/${props.productId}/assets`)).data
}

async function upload(files: FileList | File[] | null | undefined) {
  if (!files || !files.length) return
  const fd = new FormData()
  Array.from(files).slice(0, 20).forEach(f => fd.append('files', f))
  if (kind.value) fd.append('kind', kind.value)
  uploading.value = true
  try {
    const { data } = await api.post(`/products/${props.productId}/assets`, fd, { timeout: 5 * 60_000 })
    toast('good', `${data.added.length} file(s) uploaded`, data.rejected.length ? `${data.rejected.length} rejected: ${data.rejected.map((r: any) => r.error).join('; ')}` : undefined)
    load()
  } catch (e: any) {
    const rejected = e.response?.data?.rejected as { file: string; error: string }[] | undefined
    toast('bad', 'Upload rejected', rejected?.map(r => `${r.file}: ${r.error}`).join(' ') ?? errorText(e))
  } finally {
    uploading.value = false
    if (input.value) input.value.value = ''
  }
}

async function makePrimary(a: Asset) {
  await api.post(`/products/${props.productId}/assets/${a.id}/primary`)
  toast('good', 'Main image updated')
  load()
}
async function remove(a: Asset) {
  if (confirmDelete.value !== a.id) { confirmDelete.value = a.id; return }
  try {
    await api.delete(`/products/${props.productId}/assets/${a.id}`)
    toast('info', 'Deleted', a.fileName)
    confirmDelete.value = null
    load()
  } catch (e) { toast('bad', 'Delete failed', errorText(e)) }
}
onMounted(load)
</script>

<template>
  <div class="stack" style="gap: 1rem">
    <div class="dropzone" :class="{ over }" tabindex="0" role="button" aria-label="Upload images or documents"
      @click="input?.click()" @keydown.enter="input?.click()"
      @dragover.prevent="over = true" @dragleave="over = false" @drop.prevent="over = false; upload($event.dataTransfer?.files)">
      <span class="dropzone-icon"><Icon :name="uploading ? 'refresh' : 'upload'" :size="20" :class="{ spin: uploading }" /></span>
      <b style="color: var(--text)">{{ uploading ? 'Uploading to Blob storage…' : 'Drop images or PDFs here, or click to choose' }}</b>
      <span>JPEG · PNG · WebP · GIF (≤ 10 MB) · PDF (≤ 50 MB) · checked by content, not file name</span>
      <input ref="input" type="file" multiple accept="image/jpeg,image/png,image/webp,image/gif,application/pdf" hidden @change="upload(($event.target as HTMLInputElement).files)" />
    </div>
    <div class="row">
      <label for="mk" style="margin: 0">Store PDFs as</label>
      <select id="mk" v-model="kind" @click.stop>
        <option value="">Detect from file name</option><option>Datasheet</option><option>Manual</option><option>EnergyLabel</option><option>Certificate</option><option>Drawing</option>
      </select>
    </div>

    <div v-if="images.length" class="grid" style="grid-template-columns: repeat(auto-fill, minmax(130px, 1fr)); gap: .7rem">
      <div v-for="img in images" :key="img.id" class="card" style="overflow: hidden; box-shadow: none" :style="img.isPrimary ? 'border-color: var(--primary)' : ''">
        <img :src="img.thumbnailUrl ?? img.url" :alt="img.title" loading="lazy" style="display: block; width: 100%; aspect-ratio: 1; object-fit: cover; background: var(--surface-3)" />
        <div style="padding: .4rem .5rem" class="stack">
          <span class="muted" style="font-size: .72rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis" :title="img.fileName">{{ img.fileName }}</span>
          <div class="row" style="gap: .2rem">
            <span v-if="img.isPrimary" class="badge primary" style="font-size: .66rem">Main</span>
            <button v-else class="btn-ghost btn-sm" style="padding: .15rem .35rem; font-size: .72rem" @click="makePrimary(img)">Make main</button>
            <span class="spacer" />
            <button class="btn-ghost btn-sm" style="padding: .15rem .3rem" :title="confirmDelete === img.id ? 'Click again to delete' : 'Delete'" @click="remove(img)">
              <Icon name="trash" :size="13" :style="{ color: confirmDelete === img.id ? 'var(--bad)' : 'var(--muted)' }" />
            </button>
          </div>
        </div>
      </div>
    </div>

    <div class="list" style="border: 1px solid var(--border); border-radius: var(--radius)">
      <div v-for="d in documents" :key="d.id" class="list-item">
        <span class="list-icon"><Icon name="file" :size="15" /></span>
        <div class="list-main"><a :href="d.url" target="_blank" rel="noopener" class="list-title">{{ d.title }}</a>
          <div class="list-sub">{{ d.kind }} · {{ fmt.bytes(d.sizeBytes) }} · {{ d.uploadedBy }} · {{ fmt.ago(d.uploadedAtUtc) }}</div></div>
        <button class="btn-ghost btn-sm" :title="confirmDelete === d.id ? 'Click again to delete' : 'Delete'" @click="remove(d)">
          <Icon name="trash" :size="13" :style="{ color: confirmDelete === d.id ? 'var(--bad)' : 'var(--muted)' }" />
        </button>
      </div>
      <div v-if="!documents.length" class="list-item muted" style="font-size: .82rem">No documents. Add datasheets, manuals, ErP energy labels or certificates.</div>
    </div>
  </div>
</template>
