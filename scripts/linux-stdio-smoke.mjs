#!/usr/bin/env node

import {randomBytes} from 'node:crypto';
import {existsSync} from 'node:fs';
import {mkdtemp,rm} from 'node:fs/promises';
import {createServer} from 'node:net';
import {tmpdir} from 'node:os';
import {delimiter,resolve} from 'node:path';
import {createInterface} from 'node:readline';
import {spawn} from 'node:child_process';

const EXPECTED_TOOL_COUNT=68;
const REQUIRED_TOOLS=new Set([
  'GetIdentityApplicationProperties',
  'PetitionLegalIdentity',
  'SendChatMessage',
  'CreateOpenContract',
  'SignContract',
  'CreatePaymentToLegalIdUri',
  'ProcessPaymentUri'
]);

function availablePort(){
  return new Promise((resolvePort,reject)=>{
    const server=createServer();
    server.once('error',reject);
    server.listen(0,'127.0.0.1',()=>{
      const address=server.address();
      server.close(error=>error?reject(error):resolvePort(address.port));
    });
  });
}

async function withTimeout(promise,milliseconds,message){
  let timer;
  try{
    return await Promise.race([
      promise,
      new Promise((_,reject)=>{timer=setTimeout(()=>reject(new Error(message)),milliseconds);})
    ]);
  }finally{
    clearTimeout(timer);
  }
}

async function main(){
  const dll=resolve(process.argv[2]??'');
  const timeoutSeconds=Number(process.argv[3]??90);
  if(!existsSync(dll))throw new Error(`Published assembly not found: ${dll}`);
  if(!Number.isFinite(timeoutSeconds)||timeoutSeconds<=0)throw new Error('Timeout must be a positive number');

  const dataDir=await mkdtemp(resolve(tmpdir(),'neuroali-linux-smoke-'));
  const child=spawn('dotnet',[dll,'-d',dataDir,'-p',String(await availablePort())],{
    env:{...process.env,FILES_DB_SALT:randomBytes(32).toString('hex')},
    stdio:['pipe','pipe','pipe']
  });
  const pending=new Map();
  const diagnostics=[];
  let fatal;

  function fail(error){
    fatal=error;
    for(const {reject} of pending.values())reject(error);
    pending.clear();
  }

  createInterface({input:child.stderr}).on('line',line=>{
    diagnostics.push(line);
    if(diagnostics.length>20)diagnostics.shift();
  });
  createInterface({input:child.stdout}).on('line',line=>{
    let message;
    try{message=JSON.parse(line);}catch{fail(new Error(`Non-JSON STDOUT from NeuroAli: ${line}`));return;}
    if(message.method&&message.id!==undefined){fail(new Error(`Unexpected server request during smoke test: ${message.method}`));return;}
    if(message.id===undefined)return;
    const request=pending.get(message.id);
    if(!request){fail(new Error(`Unexpected JSON-RPC response id: ${message.id}`));return;}
    pending.delete(message.id);
    if(message.error)request.reject(new Error(`JSON-RPC request ${message.id} failed: ${JSON.stringify(message.error)}`));
    else request.resolve(message.result??{});
  });
  child.once('error',fail);
  child.once('exit',code=>{
    if(pending.size)fail(new Error(`NeuroAli exited with code ${code}.\n${diagnostics.join(delimiter)}`));
  });

  function send(message){
    if(fatal)throw fatal;
    child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  async function request(id,method,params){
    const response=new Promise((resolveRequest,reject)=>pending.set(id,{resolve:resolveRequest,reject}));
    send({jsonrpc:'2.0',id,method,params});
    return withTimeout(response,timeoutSeconds*1000,
      `Timed out waiting for ${method}.\n${diagnostics.join(delimiter)}`);
  }

  try{
    const initialized=await request(1,'initialize',{
      protocolVersion:'2025-06-18',
      capabilities:{},
      clientInfo:{name:'neuroali-linux-smoke',version:'1.0.0'}
    });
    if(!initialized.serverInfo)throw new Error('Initialize response did not contain serverInfo');

    send({jsonrpc:'2.0',method:'notifications/initialized',params:{}});
    const listed=await request(2,'tools/list',{});
    if(!Array.isArray(listed.tools))throw new Error('tools/list response did not contain a tools array');

    const names=new Set(listed.tools.map(tool=>tool.name));
    if(listed.tools.length!==EXPECTED_TOOL_COUNT)
      throw new Error(`Expected ${EXPECTED_TOOL_COUNT} tools, received ${listed.tools.length}`);
    const missing=[...REQUIRED_TOOLS].filter(name=>!names.has(name));
    if(missing.length)throw new Error(`Missing ReliefMesh-required tools: ${missing.join(', ')}`);

    console.log('LINUX_STDIO_INITIALIZE=PASS');
    console.log('LINUX_TOOLS_LIST=PASS');
    console.log(`LINUX_TOOL_COUNT=${listed.tools.length}`);
    console.log('RELIEFMESH_REQUIRED_TOOLS=PASS');
  }finally{
    child.stdin.end();
    if(child.exitCode===null){
      const exited=new Promise(resolveExit=>child.once('exit',resolveExit));
      await withTimeout(exited,20_000,'shutdown').catch(()=>child.kill('SIGTERM'));
    }
    await rm(dataDir,{recursive:true,force:true});
  }
}

main().catch(error=>{
  console.error(error.message);
  process.exitCode=1;
});