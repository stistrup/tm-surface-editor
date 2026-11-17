import { BufferAttribute, BufferGeometry, CanvasTexture, FrontSide, MeshBasicMaterial, SRGBColorSpace } from 'three'
import type { InstanceInfo } from '../api'

type V3 = [number, number, number]

// a cell's six sides: outward normal, then corners bottom-left, bottom-right, top-right, top-left as seen from outside
const sides: { normal: V3; corners: V3[] }[] = [
  { normal: [1, 0, 0], corners: [[1, 0, 1], [1, 0, 0], [1, 1, 0], [1, 1, 1]] },
  { normal: [-1, 0, 0], corners: [[0, 0, 0], [0, 0, 1], [0, 1, 1], [0, 1, 0]] },
  { normal: [0, 0, 1], corners: [[0, 0, 1], [1, 0, 1], [1, 1, 1], [0, 1, 1]] },
  { normal: [0, 0, -1], corners: [[1, 0, 0], [0, 0, 0], [0, 1, 0], [1, 1, 0]] },
  { normal: [0, 1, 0], corners: [[0, 1, 1], [1, 1, 1], [1, 1, 0], [0, 1, 0]] },
  { normal: [0, -1, 0], corners: [[1, 0, 1], [0, 0, 1], [0, 0, 0], [1, 0, 0]] },
]

// a forced camera's trigger: the outside of its cells only, each face showing the label once
export const triggerGeometry = (info: InstanceInfo): BufferGeometry => {
  const size = info.cellSize as V3
  const cells = info.cells as V3[]
  const filled = new Set(cells.map((c) => c.join(',')))
  const positions: number[] = []
  const normals: number[] = []
  const uvs: number[] = []
  const indices: number[] = []
  for (const cell of cells)
    for (const { normal, corners } of sides) {
      if (filled.has(cell.map((v, i) => v + normal[i]).join(','))) continue
      const base = positions.length / 3
      corners.forEach((corner, k) => {
        positions.push(...corner.map((v, i) => (cell[i] + v) * size[i]))
        normals.push(...normal)
        uvs.push(k === 1 || k === 2 ? 1 : 0, k >= 2 ? 1 : 0)
      })
      indices.push(base, base + 1, base + 2, base, base + 2, base + 3)
    }
  const geometry = new BufferGeometry()
  geometry.setAttribute('position', new BufferAttribute(new Float32Array(positions), 3))
  geometry.setAttribute('normal', new BufferAttribute(new Float32Array(normals), 3))
  geometry.setAttribute('uv', new BufferAttribute(new Float32Array(uvs), 2))
  geometry.setIndex(indices)
  return geometry
}

// see-through fill with the label; front faces only, or the far side's labels show mirrored through it; disabled: grey
export const triggerMaterial = (label: string, disabled: boolean) => {
  const canvas = document.createElement('canvas')
  canvas.width = 256
  canvas.height = 192
  const g = canvas.getContext('2d')!
  g.fillStyle = disabled ? 'rgba(150, 150, 150, 0.35)' : 'rgba(190, 120, 255, 0.5)'
  g.fillRect(0, 0, canvas.width, canvas.height)
  // as large as fits the face
  g.font = 'bold 56px sans-serif'
  const fits = (canvas.width * 0.88) / g.measureText(label).width
  if (fits < 1) g.font = `bold ${Math.floor(56 * fits)}px sans-serif`
  g.textAlign = 'center'
  g.textBaseline = 'middle'
  g.lineWidth = 6
  g.strokeStyle = disabled ? 'rgba(60, 60, 60, 0.6)' : 'rgba(60, 20, 110, 0.8)'
  g.fillStyle = disabled ? 'rgba(235, 235, 235, 0.8)' : '#ffffff'
  g.strokeText(label, canvas.width / 2, canvas.height / 2)
  g.fillText(label, canvas.width / 2, canvas.height / 2)
  const map = new CanvasTexture(canvas)
  map.colorSpace = SRGBColorSpace
  map.anisotropy = 4
  const material = new MeshBasicMaterial({ map, transparent: true, depthWrite: false, side: FrontSide, fog: false })
  // instance colours carry the block colour index (overlays.ts ghostMaterial)
  material.onBeforeCompile = (shader) => {
    shader.fragmentShader = shader.fragmentShader.replace('#include <color_fragment>', '')
  }
  return material
}
