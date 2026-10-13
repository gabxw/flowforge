import type { Page } from '@playwright/test'

export async function dragNode(page:Page,id:string,dx=70,dy=50) {
  const card=page.getByTestId('canvas-node-'+id)
  await card.scrollIntoViewIfNeeded()
  const box=await card.boundingBox()
  if(!box) throw new Error('Card não foi renderizado')
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2)
  await page.mouse.down()
  await page.mouse.move(box.x+box.width/2+dx,box.y+box.height/2+dy,{steps:12})
  await page.mouse.up()
}
export async function connectPorts(page:Page,source:string,port:string,target:string) {
  const canvas=page.getByTestId('workflow-canvas')
  await canvas.scrollIntoViewIfNeeded()
  const from=await page.getByTestId('output-'+source+'-'+port).boundingBox()
  const to=await page.getByTestId('input-'+target).boundingBox()
  if(!from||!to) throw new Error('Portas não foram renderizadas')
  await page.mouse.move(from.x+from.width/2,from.y+from.height/2)
  await page.mouse.down()
  await page.mouse.move(to.x+to.width/2,to.y+to.height/2,{steps:16})
  await page.mouse.up()
}
