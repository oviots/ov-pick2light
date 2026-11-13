'use strict';
// Send a scoped batch_finish only. No DB writes, no synthetic Confirm.
const fs = require('node:fs');
const crypto = require('node:crypto');
const {MongoClient} = require('mongoose').mongo;
const mqtt = require('mqtt');
const [blockNo, sessionCode] = process.argv.slice(2);
if (!['01','02'].includes(blockNo) || !sessionCode) throw Error('Use: node scripts/uat-3lights/finish-session.cjs 01 SESSION_CODE');
const config = JSON.parse(fs.readFileSync('/Volumes/GTeck/Projects/1shot/workspaces/mml/rocket-wms-service/server/modules/default/conf.json','utf8'));
const endpoint = new URL(config.MONGO_URI);
if(endpoint.hostname !== '192.168.12.6' || endpoint.port !== '37017') throw Error('Expected UAT endpoint');
const client = new MongoClient(config.MONGO_URI,{serverSelectionTimeoutMS:5000});
async function main() {
    let broker;
    try {
        await client.connect();
        const db = client.db('sft_uat_p2l_local_3lights_20261113');
        const session = await db.collection('P2L.PickingSessions').findOne({Code:sessionCode,BlockCode:blockNo,
            RocketCode:'P2L-RELIGHT-20261113-01',Status:'InProgress',IsDeleted:0});
        if (!session) throw Error('Not an active session in the prepared relight test');
        const block = await db.collection('P2L.Blocks').findOne({Code:blockNo,Status:'Active',IsDeleted:0});
        const cfg = block?.MQTTConfig;
        if(cfg?.broker!=='s20a2471.ala.asia-southeast1.emqxsl.com'||cfg.prefixTopic!=='p2l/uat/test01') throw Error('Unexpected test broker/topic');
        broker = await mqtt.connectAsync(`mqtts://${cfg.broker}:${cfg.port}`,{
            clientId:`p2l-relight-test-${crypto.randomUUID()}`,username:cfg.username,password:cfg.password,
            reconnectPeriod:0,connectTimeout:10000,rejectUnauthorized:true,...(cfg.tls?.ca?{ca:cfg.tls.ca}:{}),
        });
        const command = {type:'batch_finish',blockNo,correlationId:sessionCode};
        let timeout;
        try {
            await Promise.race([
                broker.publishAsync(`${cfg.prefixTopic}/commands`,JSON.stringify(command),{qos:1,retain:false}),
                new Promise((_,reject)=>{timeout=setTimeout(()=>reject(Error('Publish timed out')),10000);}),
            ]);
        } finally { clearTimeout(timeout); }
        console.log(JSON.stringify({published:command,databaseChanged:false,topic:`${cfg.prefixTopic}/commands`}));
        console.log('Broker accepted the command. Check P2L log for Controller batch_finish success.');
    } finally {
        if(broker) await broker.endAsync(true);
        await client.close();
    }
}
main().catch(error=>{console.error(error.name,error.message?.replace(config.MONGO_URI,'[Mongo URI]'));process.exitCode=1;});
