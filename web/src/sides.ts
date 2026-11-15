// a pick's sides: "<material>/<sides>:<way>" (ConversionRules.SplitChoice); bare ways are ice
const DEFAULT_MATERIAL = 'ice'

const split = (pick: string) => {
  const at = pick.indexOf(':')
  if (at <= 0) return { material: null, sides: null, way: pick }
  const [material, sides] = pick.slice(0, at).split('/')
  return { material, sides: sides ?? null, way: pick.slice(at + 1) }
}

export const sidesOf = (pick: string) => split(pick).sides

// the pick without its sides, as picks.jsonc lists it
export const baseOf = (pick: string) => {
  const { material, way } = split(pick)
  return material === null || material === DEFAULT_MATERIAL ? way : `${material}:${way}`
}

// null: the material's own sides
export const withSides = (pick: string, sides: string | null) => {
  const base = baseOf(pick)
  if (sides === null) return base
  const { material, way } = split(base)
  return `${material ?? DEFAULT_MATERIAL}/${sides}:${way}`
}
