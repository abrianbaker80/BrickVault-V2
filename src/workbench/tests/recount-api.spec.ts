import { expect } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { test } from './trusted'
import { fixture } from './bin-fixture'
import type { components } from '../src/schema'
type Models = components['schemas']
const root = '/api/v1/inventory/recounts/'

test('recount snapshots cover every match and concurrent confirmations have one actionable task per position', async ({page})=>{
  const f = await fixture(page)
  const post = (path:string,data:object)=>page.request.post(root+path,{headers:f.headers,data})
  const previewId = randomUUID()
  const shipment = await f.write('receiving/purchases',0,{reference:'RQ-'+f.a.code,source:'Fictional bulk fixture',
    currency:'USD',orderedQuantity:28,pendingChargeMinor:null})
  const delivery = await f.write('receiving/deliveries',1,{purchaseId:shipment,reference:'28 lots',catalogId:f.catalogId,quantity:28,itemCostMinor:null})
  for (let i=0;i<26;i++) await f.write('receiving/putaway',2+i,{purchaseId:shipment,deliveryId:delivery,catalogId:f.catalogId,
    locationId:f.a.id,confirmedLocationCode:f.a.code,quantity:1})
  const request = {previewId,scope:'matching',q:'RQ-'+f.a.code,itemType:'part',locationId:f.a.id,positionIds:[]}
  const frozen = await post('previews',request)
  expect(frozen.status()).toBe(200)
  const first:Models['RecountPreviewPage'] = await frozen.json()
  expect(first.total).toBe(26)
  expect(first.rows).toHaveLength(25)
  const last:Models['RecountPreviewPage'] = await (await page.request.get(root+'previews/'+previewId+'?after='+first.nextAfter)).json()
  expect(last.rows).toHaveLength(1)
  expect(last.nextAfter).toBeNull()
  await f.write('receiving/putaway',28,{purchaseId:shipment,deliveryId:delivery,catalogId:f.catalogId,locationId:f.a.id,confirmedLocationCode:f.a.code,quantity:1})
  expect((await (await post('previews',request)).json()).total).toBe(26)
  expect((await post('previews',{...request,q:'different'})).status()).toBe(409)
  const command={commandId:randomUUID(),previewId}
  const confirmed = await post('batches',command)
  expect(confirmed.status()).toBe(200)
  const diagnostic = await confirmed.json()
  await page.goto('/#activity')
  await page.getByLabel('Correlation ID').fill(diagnostic.correlationId)
  await page.getByRole('button', {name:'Look up command'}).click()
  await expect(page.getByText(command.commandId, {exact:true})).toBeVisible()
  const read = async (id:string):Promise<Models['RecountBatchPage']> => (await page.request.get(root+'batches/'+id)).json()
  expect((await read(previewId)).changed).toBe(26)
  expect((await read(previewId)).total).toBe(26)

  const missing = randomUUID()
  const selection = {scope:'selected',positionIds:[f.position,missing],q:null,itemType:null,locationId:null}
  const firstBatch=randomUUID(),secondBatch=randomUUID()
  for(const id of [firstBatch,secondBatch]) expect((await post('previews',{...selection,previewId:id})).status()).toBe(200)
  const cmds=[firstBatch,secondBatch].map(id=>({commandId:randomUUID(),previewId:id}))
  const saved=await Promise.all(cmds.map(c=>post('batches',c)))
  expect(saved.map(r=>r.status())).toEqual([200,200])
  const receipts=await Promise.all([read(firstBatch),read(secondBatch)])
  expect(receipts.map(b=>b.queued).sort()).toEqual([0,1])
  expect(receipts.map(b=>b.alreadyQueued).sort()).toEqual([0,1])
  expect(receipts.map(b=>b.unavailable)).toEqual([1,1])
  const replay=await post('batches',cmds[0])
  expect(await replay.json()).toEqual(await saved[0].json())
  const countFields={purchaseId:f.paid.purchaseId,positionId:f.position,locationId:f.a.id,confirmedLocationCode:f.a.code,
    countedQuantity:6,reason:'Witness the queued position'}
  for (const batch of receipts) {
    expect(batch.rows.find(r=>r.target.positionId===f.position)?.taskState).toBe('pending')
    expect(batch.rows.find(r=>r.target.positionId===missing)?.taskState).toBeNull()
  }
  const observation=await f.write('receiving/counts',5,countFields)
  for(const id of [firstBatch,secondBatch]) expect((await read(id)).rows.find(r=>r.target.positionId===f.position)).toMatchObject({observationId:observation,taskState:'witnessed'})
  const fresh=randomUUID()
  await post('previews',{...selection,previewId:fresh,positionIds:[f.position]})
  await post('batches',{commandId:randomUUID(),previewId:fresh})
  expect((await read(fresh)).rows[0].observationId).toBeNull()
  const duplicate=randomUUID()
  await post('previews',{...selection,previewId:duplicate,positionIds:[f.position]})
  await post('batches',{commandId:randomUUID(),previewId:duplicate})
  const queue = async ():Promise<Models['RecountQueuePage']> => (await page.request.get(root+'queue?limit=100')).json()
  const before=await queue()
  await f.write('receiving/transfers',6,{purchaseId:f.paid.purchaseId,sourcePlacementId:f.position,sourceLocationId:f.a.id,
    confirmedSourceLocationCode:f.a.code,destinationLocationId:f.b.id,confirmedDestinationLocationCode:f.b.code,
    quantity:6,moveAllBacking:true,backing:[],reason:'Move the full queued position'})
  const after=await queue()
  expect(after.total).toBe(before.total-1)
  expect(after.rows.map(r=>r.target.positionId)).not.toContain(f.position)
  expect((await read(fresh)).rows[0]).toMatchObject({outcome:'queued',taskState:'depleted',observationId:null})
  expect((await read(duplicate)).rows[0]).toMatchObject({outcome:'already-queued',taskState:'depleted',observationId:null,queueBatchId:fresh})
  expect((await read(firstBatch)).rows.find(r=>r.target.positionId===f.position)?.taskState).toBe('witnessed')
})
