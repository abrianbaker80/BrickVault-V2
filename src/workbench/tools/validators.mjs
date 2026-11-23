import { readFileSync, writeFileSync } from 'node:fs'
import Ajv2020 from 'ajv/dist/2020.js'
import addFormats from 'ajv-formats'
import standaloneCode from 'ajv/dist/standalone/index.js'

const contract = JSON.parse(readFileSync(new URL('../../BrickVault.Api/openapi.v1.json', import.meta.url), 'utf8'))
const ajv = new Ajv2020({ code: { source: true }, formats: { int32: true } })
addFormats(ajv)
ajv.addVocabulary(['components'])
const schemas = contract.components.schemas
const schemaId = 'https://brickvault.invalid/v1'
ajv.addSchema({ $id: schemaId, components: { schemas } })
const entries = Object.keys(schemas).map(name => [name, schemaId + '#/components/schemas/' + name])
const directory = new URL('../src/', import.meta.url)
writeFileSync(new URL('validators.cjs', directory), standaloneCode(ajv, Object.fromEntries(entries)))
const types = entries.map(([name]) =>
  "export function " + name + "(value: unknown): value is components['schemas']['" + name + "']"
)
writeFileSync(new URL('validators.d.cts', directory), "import type { components } from './schema'\n" + types.join('\n') + '\n')
