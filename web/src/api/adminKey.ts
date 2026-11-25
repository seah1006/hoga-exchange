const storageKey = 'hoga.adminKey'

export function readAdminKey() {
  return sessionStorage.getItem(storageKey) ?? ''
}

export function writeAdminKey(value: string) {
  if (value) sessionStorage.setItem(storageKey, value)
  else sessionStorage.removeItem(storageKey)
}
