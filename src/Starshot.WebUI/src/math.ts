export const PHI = (1 + Math.sqrt(5)) / 2
export const PI = Math.PI
// Deterministic, bounded variation. No random layout shifts across sessions.
export function detail(index: number) {
  const fraction = Math.sin((index + 1) * PI * PHI) * 10000
  return fraction - Math.floor(fraction)
}
export const motion = { enter: Math.round(PI * 100), small: Math.round(PI * 100 / PHI) }
export const capacityFromStop = (stop: number) => Math.round(2 ** stop * 10) / 10
export const stopFromCapacity = (capacity: number) => Math.log2(capacity)
