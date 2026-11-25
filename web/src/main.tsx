import { createRoot } from 'react-dom/client'
import { App } from './App'
import { DEFAULT_SPEED, MockMarketSource } from './api/mockMarketSource'
import './styles.css'

const useMock = import.meta.env.VITE_USE_MOCK ?? 'true'
const configuredSpeed = Number(import.meta.env.VITE_MOCK_SPEED ?? DEFAULT_SPEED)
const speed = Number.isFinite(configuredSpeed) && configuredSpeed > 0 ? configuredSpeed : DEFAULT_SPEED
const root = createRoot(document.getElementById('root')!)
root.render(useMock === 'true' ? <App source={new MockMarketSource(42, speed)} speed={speed} /> : <main><h1>호가 거래소</h1><p>실제 서버 연결은 아직 지원하지 않습니다. VITE_USE_MOCK=true로 실행하세요.</p></main>)
