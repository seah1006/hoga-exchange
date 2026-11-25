import js from '@eslint/js'
import hooks from 'eslint-plugin-react-hooks'
import ts from 'typescript-eslint'

export default ts.config(
  { ignores: ['dist', '.npm-cache'] },
  js.configs.recommended,
  ...ts.configs.recommended,
  { files: ['**/*.{ts,tsx}'], plugins: { 'react-hooks': hooks }, rules: hooks.configs.recommended.rules },
)
