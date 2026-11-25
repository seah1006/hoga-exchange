import { createRoot } from 'react-dom/client'
import { App } from './App'
import { DEFAULT_SPEED, MockMarketSource } from './api/mockMarketSource'
import { ServerMarketSource } from './api/serverMarketSource'
import './styles.css'

const useMock = import.meta.env.VITE_USE_MOCK ?? 'true'
const configuredSpeed = Number(import.meta.env.VITE_MOCK_SPEED ?? DEFAULT_SPEED)
const speed = Number.isFinite(configuredSpeed) && configuredSpeed > 0 ? configuredSpeed : DEFAULT_SPEED
const root = createRoot(document.getElementById('root')!)
root.render(useMock === 'true' ? <App source={new MockMarketSource(42, speed)} speed={speed} /> : <App source={new ServerMarketSource({ base: import.meta.env.VITE_API_BASE })} />)
