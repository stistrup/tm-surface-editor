import { acceptHMRUpdate, defineStore } from 'pinia'
import { computed, ref, shallowRef, watch } from 'vue'
import { fetchGeometry, fetchMaps, fetchScene, type InstanceInfo, type MapEntry, type SceneManifest } from '../api'
import { readJson, readText, remembered, writeJson, writeText } from '../storage'
import { categoryOf, type Category } from '../viewer/categories'
import type { CameraState, MapScene } from '../viewer/MapScene'

export interface LoadedScene {
  manifest: SceneManifest
  geometry: ArrayBuffer
}

const MAP_KEY = 'current-map'
const SHOWN_KEY = 'maps-shown'
const HIDDEN_KEY = 'hidden-categories'
const VIEW_KEY = 'view:'
const CHOSEN_KEY = 'chosen-maps'

// per map file: where the camera was and what was selected; only while the file is unchanged (indices follow the file)
export interface ViewMemory {
  modified: string
  selection: number[]
  camera: CameraState | null
}

export const useMapStore = defineStore('map', () => {
  const maps = ref<MapEntry[]>([])
  const mapsError = ref('')
  const currentMap = ref<MapEntry | null>(null)
  const scene = shallowRef<LoadedScene | null>(null)
  const status = ref('')
  const loadError = ref('')
  const hovered = ref<number | null>(null)
  const selection = ref<number[]>([])
  // map paths ticked in the list, for converting several at once
  const chosenMaps = ref(new Set<string>(readJson<string[]>(CHOSEN_KEY, [])))
  watch(chosenMaps, (chosen) => writeJson(CHOSEN_KEY, [...chosen]), { deep: true })
  const choose = (paths: string[], on: boolean) => {
    for (const path of paths) {
      if (on) chosenMaps.value.add(path)
      else chosenMaps.value.delete(path)
    }
  }
  // the last picked piece, the one the properties panel shows
  const selected = computed<number | null>({
    get: () => selection.value.at(-1) ?? null,
    set: (index) => (selection.value = index === null ? [] : [index]),
  })

  // an expandable gate is one object over several cells: the same block, same direction, in touching cells
  const cellKey = (info: InstanceInfo) => `${info.name}|${info.direction}|${(info.cell as number[]).join(',')}`
  const gateCells = computed(() => {
    const cells = new Map<string, number>()
    infos.value.forEach((info, i) => {
      if (info.kind === 'block' && info.name.startsWith('GateExpandable') && Array.isArray(info.cell)) cells.set(cellKey(info), i)
    })
    return cells
  })
  // free gates: cell centre and axes from the placement (a 180° turned copy still lines up)
  const freeGates = computed(() => {
    const instances = scene.value?.manifest.instances ?? []
    return instances.flatMap(({ info, m }, i) => {
      if (info.kind !== 'block' || !info.name.startsWith('GateExpandable') || !Array.isArray(info.position)) return []
      const axis = (row: number) => [m[row * 4], m[row * 4 + 1], m[row * 4 + 2]]
      const [x, y, z, t] = [axis(0), axis(1), axis(2), axis(3)]
      const centre = t.map((v, k) => v + 16 * x[k] + 4 * y[k] + 16 * z[k])
      return [{ index: i, name: info.name, centre, x, y, z }]
    })
  })
  type FreeGate = (typeof freeGates.value)[number]
  const dot = (a: number[], b: number[]) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
  const touching = (a: FreeGate, b: FreeGate) => {
    if (a.name !== b.name || Math.abs(dot(a.x, b.x)) < 0.999 || dot(a.y, b.y) < 0.999) return false
    const d = b.centre.map((v, k) => v - a.centre[k])
    const steps = [Math.abs(dot(d, a.x)) / 32, Math.abs(dot(d, a.y)) / 8, Math.abs(dot(d, a.z)) / 32]
    return steps.filter((s) => Math.abs(s - 1) < 0.01).length === 1 && steps.filter((s) => s < 0.01).length === 2
  }
  const wholeFreeGate = (index: number): number[] | null => {
    const gates = freeGates.value
    const start = gates.find((g) => g.index === index)
    if (!start) return null
    const found = new Set([start])
    const queue = [start]
    for (let gate; (gate = queue.pop()); )
      for (const other of gates)
        if (!found.has(other) && touching(gate, other)) {
          found.add(other)
          queue.push(other)
        }
    return [...found].map((g) => g.index).filter((i) => i !== index).concat(index)
  }

  // the piece with the rest of its object, the piece itself last
  const whole = (index: number): number[] => {
    const info = infos.value[index]
    // a forced camera's trigger areas
    if (info?.kind === 'camera') {
      const areas = infos.value.flatMap((other, i) => (i !== index && other.kind === 'camera' && other.camera === info.camera ? [i] : []))
      return [...areas, index]
    }
    const free = wholeFreeGate(index)
    if (free) return free
    if (info?.kind !== 'block' || !Array.isArray(info.cell) || !gateCells.value.has(cellKey(info))) return [index]
    const found = new Set([index])
    const queue = [info.cell as number[]]
    for (let cell; (cell = queue.pop()); ) {
      for (const [dx, dy, dz] of [[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]]) {
        const next = [cell[0] + dx, cell[1] + dy, cell[2] + dz]
        const i = gateCells.value.get(`${info.name}|${info.direction}|${next.join(',')}`)
        if (i === undefined || found.has(i)) continue
        found.add(i)
        queue.push(next)
      }
    }
    found.delete(index)
    return [...found, index]
  }

  // click: just this one (again: none); shift-click: add or remove it
  const pick = (index: number | null, add: boolean) => {
    if (index === null) {
      if (!add) selection.value = []
      return
    }
    const all = whole(index)
    if (add) {
      selection.value = selection.value.includes(index)
        ? selection.value.filter((i) => !all.includes(i))
        : [...selection.value.filter((i) => !all.includes(i)), ...all]
    } else {
      const only = selection.value.length === all.length && all.every((i) => selection.value.includes(i))
      selection.value = only ? [] : all
    }
  }
  const paint = (index: number, add: boolean) => {
    const all = whole(index)
    const rest = selection.value.filter((i) => !all.includes(i))
    const next = add ? [...rest, ...all] : rest
    if (next.length !== selection.value.length || add && !selection.value.includes(index)) selection.value = next
  }
  const view = shallowRef<MapScene | null>(null)
  const textureLoads = ref(0)
  const cameraMoves = ref(0)
  const hidden = ref<Category[]>(readJson<Category[]>(HIDDEN_KEY, []))
  watch(hidden, (value) => writeJson(HIDDEN_KEY, value))

  const viewMemory = (map: MapEntry): ViewMemory | null => {
    const memory = readJson<ViewMemory | null>(VIEW_KEY + map.path, null)
    return memory?.modified === map.modified ? memory : null
  }

  const rememberView = (patch: Partial<ViewMemory>) => {
    const map = currentMap.value
    if (!map || !scene.value) return
    const memory = viewMemory(map) ?? { modified: map.modified, selection: [], camera: null }
    writeJson(VIEW_KEY + map.path, { ...memory, ...patch, modified: map.modified })
  }

  watch(selection, (value) => rememberView({ selection: value }))
  const mapsShown = remembered(SHOWN_KEY, true)
  // bumped to make the maps panel open on the current map
  const revealed = ref(0)
  const revealMap = () => {
    mapsShown.value = true
    revealed.value++
  }


  let loadId = 0

  const rememberMap = (path: string) => writeText(MAP_KEY, path)
  const forgetMap = () => writeText(MAP_KEY, null)
  const rememberedMap = () => readText(MAP_KEY)

  // instances, placeholders, boxes, then cameras: the order MapScene numbers them in
  const infos = computed<InstanceInfo[]>(() => {
    const manifest = scene.value?.manifest
    if (!manifest) return []
    return [...manifest.instances, ...manifest.placeholders, ...manifest.boxes, ...(manifest.cameras ?? [])].map((i) => i.info)
  })

  const categoryCounts = computed(() => {
    const counts = new Map<Category, number>()
    for (const info of infos.value) {
      const category = categoryOf(info)
      counts.set(category, (counts.get(category) ?? 0) + 1)
    }
    return counts
  })

  const loadMaps = async () => {
    try {
      maps.value = await fetchMaps()
      mapsError.value = ''
      const remembered = maps.value.find((map) => map.path === rememberedMap())
      if (remembered && !currentMap.value) void openMap(remembered)
    } catch (error) {
      mapsError.value = String(error)
    }
  }

  const openMap = async (map: MapEntry, rebuild = false) => {
    const id = ++loadId
    currentMap.value = map
    rememberMap(map.path)
    scene.value = null
    selected.value = null
    hovered.value = null
    loadError.value = ''
    try {
      status.value = rebuild ? 'Rebuilding scene…' : 'Reading the map…'
      const manifest = await fetchScene(map.path, rebuild)
      if (id !== loadId) return
      status.value = 'Loading geometry…'
      const geometry = await fetchGeometry(map.path, manifest.geometryBytes, (fraction) => {
        if (id === loadId) status.value = `Loading geometry… ${Math.round(fraction * 100)}%`
      })
      if (id !== loadId) return
      scene.value = { manifest, geometry }
      selection.value = viewMemory(map)?.selection.filter((i) => i < infos.value.length) ?? []
      status.value = ''
    } catch (error) {
      if (id !== loadId) return
      status.value = ''
      loadError.value = error instanceof Error ? error.message : String(error)
    }
  }

  const closeMap = () => {
    loadId++
    currentMap.value = null
    forgetMap()
    scene.value = null
    selected.value = null
    hovered.value = null
    status.value = ''
    loadError.value = ''
  }

  const toggleCategory = (category: Category) => {
    hidden.value = hidden.value.includes(category)
      ? hidden.value.filter((c) => c !== category)
      : [...hidden.value, category]
  }

  return {
    maps,
    mapsError,
    currentMap,
    closeMap,
    scene,
    status,
    loadError,
    hovered,
    selected,
    selection,
    chosenMaps,
    choose,
    pick,
    paint,
    hidden,
    mapsShown,
    revealed,
    revealMap,
    infos,
    whole,
    categoryCounts,
    view,
    textureLoads,
    cameraMoves,
    loadMaps,
    openMap,
    toggleCategory,
    viewMemory,
    rememberView,
  }
})

if (import.meta.hot) import.meta.hot.accept(acceptHMRUpdate(useMapStore, import.meta.hot))
