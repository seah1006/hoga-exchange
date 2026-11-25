const integer = new Intl.NumberFormat('ko-KR')

export const formatPrice = (price: number) => `${integer.format(price)}원`
export const formatQuantity = (quantity: number) => `${integer.format(quantity)}주`
export function formatTime(logicalTimeMs: number) {
  const seconds = 9 * 3600 + Math.floor(logicalTimeMs / 1000)
  return [Math.floor(seconds / 3600) % 24, Math.floor(seconds / 60) % 60, seconds % 60]
    .map((part) => String(part).padStart(2, '0')).join(':')
}
