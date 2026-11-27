const pattern=/^[A-Za-z0-9][A-Za-z0-9._-]{0,47}$/
export const validLabel=(label:string)=>pattern.test(label) && !/[\r\n]/.test(label)
export const sameLabel=(a:string,b:string)=>validLabel(a) && validLabel(b) && a.toLowerCase()===b.toLowerCase()
export const registryLink=(bin:string,event?:string)=>'#locations/'+bin+'/registry'+(event ? '/'+event : '')
