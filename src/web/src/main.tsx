import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
// Tokens e componentes do design aprovado: fonte única em docs/design (sem cópia no frontend).
import '../../../docs/design/way2do.css'
import './app.css'
import App from './App'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
