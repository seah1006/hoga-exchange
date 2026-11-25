import { expect, test } from 'vitest'
import { formatPrice, formatQuantity, formatTime } from './format'

test('가격은 천 단위와 원으로 표시한다', () => expect(formatPrice(10050)).toBe('10,050원'))
test('수량은 천 단위와 주로 표시한다', () => expect(formatQuantity(3200)).toBe('3,200주'))
test('장 시작 09:00 기준 논리 시각을 초 단위로 표시한다', () => {
  expect(formatTime(0)).toBe('09:00:00')
  expect(formatTime(3_661_999)).toBe('10:01:01')
})
