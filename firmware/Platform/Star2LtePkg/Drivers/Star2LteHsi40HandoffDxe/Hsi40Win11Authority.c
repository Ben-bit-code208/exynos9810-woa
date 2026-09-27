/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "Hsi40Win11Authority.h"
#include <string.h>

static uint32_t U32(const uint8_t *p)
{
    return p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16)|((uint32_t)p[3]<<24);
}
static uint64_t U64(const uint8_t *p)
{
    return U32(p)|((uint64_t)U32(p+4)<<32);
}
static uint32_t Fail(H40_W11_AUTHORITY *r,uint32_t code,unsigned table,
    uint32_t offset,uint32_t expected,uint32_t observed)
{
    r->Result=code;r->TableIndex=table;r->Offset=offset;r->Expected=expected;r->Observed=observed;
    return code;
}
static uint32_t CheckTable(const H40_W11_POLICY *policy,unsigned index,
    H40_W11_TABLE actual,H40_W11_AUTHORITY *r)
{
    H40_W11_TABLE expected=policy->Tables[index];size_t i;uint8_t sum=0;
    if(!actual.Data||actual.Bytes!=expected.Bytes||actual.Bytes<36||
       U32(actual.Data+4)!=actual.Bytes||memcmp(actual.Data,expected.Data,4))
        return Fail(r,H40_W11_BAD_TABLE,index,4,(uint32_t)expected.Bytes,(uint32_t)actual.Bytes);
    for(i=0;i<actual.Bytes;++i)sum=(uint8_t)(sum+actual.Data[i]);
    if(sum)return Fail(r,H40_W11_CHECKSUM,index,9,0,sum);
    for(i=0;i<actual.Bytes;++i)
        if(policy->Compare[index][i]&&actual.Data[i]!=expected.Data[i])
            return Fail(r,H40_W11_IMMUTABLE,index,(uint32_t)i,expected.Data[i],actual.Data[i]);
    return H40_W11_OK;
}
static uint32_t Topology(const H40_W11_POLICY *policy,const uint8_t *apic,
    const uint8_t *dsdt,H40_W11_AUTHORITY *r)
{
    unsigned i,side;
    for(i=0;i<8;++i){
        const uint8_t *gicc=apic+44+80*i,*mat=dsdt+policy->MatOffsets[i];
        for(side=0;side<2;++side){
            const uint8_t *entry=side?mat:gicc;
            uint32_t flags=U32(entry+12),parking=U32(entry+16);
            uint64_t address=U64(entry+24);
            if((flags&~1u)||parking>1||address>0xffffffffu||(address&0xfffu)||
               (address&&!parking)||(i>=4&&((!side&&flags)||parking||address)))
                return Fail(r,H40_W11_TOPOLOGY,side?5:1,44+80*i,0,flags);
        }
        if(U32(gicc+12)&1)r->ActiveMask|=1u<<i;
    }
    if(r->ActiveMask!=1&&r->ActiveMask!=15)
        return Fail(r,H40_W11_TOPOLOGY,1,0,15,r->ActiveMask);
    return H40_W11_OK;
}
uint32_t H40Win11Authority(const H40_W11_POLICY *policy,
    const H40_W11_TABLE *root,size_t count,H40_W11_TABLE dsdt,
    uint32_t enabled,H40_W11_AUTHORITY *r)
{
    unsigned i,j,seen=0;const uint8_t *apic=NULL;
    if(!r)return H40_W11_ARGUMENT;
    memset(r,0,sizeof(*r));r->Size=sizeof(*r);r->Version=1;
    if(!enabled)return r->Result=H40_W11_DISABLED;
    if(enabled!=1||!policy||!root)return r->Result=H40_W11_ARGUMENT;
    if(count!=H40_W11_ROOT_TABLES)
        return Fail(r,H40_W11_INVENTORY,0,0,H40_W11_ROOT_TABLES,(uint32_t)count);
    for(i=0;i<H40_W11_POLICY_TABLES;++i)
        if(!policy->Tables[i].Data||!policy->Compare[i]||
           policy->Tables[i].Bytes<36||policy->Tables[i].Bytes>4096)
            return r->Result=H40_W11_ARGUMENT;
    if(policy->Tables[1].Bytes!=708||policy->Tables[5].Bytes!=1413)
        return r->Result=H40_W11_ARGUMENT;
    for(i=0;i<8;++i)
        if(policy->MatOffsets[i]>policy->Tables[5].Bytes-80)
            return r->Result=H40_W11_ARGUMENT;
    for(i=0;i<count;++i){
        if(!root[i].Data||root[i].Bytes<36)return r->Result=H40_W11_BAD_TABLE;
        for(j=0;j<H40_W11_ROOT_TABLES;++j)
            if(!memcmp(root[i].Data,policy->Tables[j].Data,4))break;
        if(j==H40_W11_ROOT_TABLES||(seen&(1u<<j)))
            return Fail(r,H40_W11_INVENTORY,i,0,0,U32(root[i].Data));
        seen|=1u<<j;
        if(CheckTable(policy,j,root[i],r))return r->Result;
        if(j==1)apic=root[i].Data;
    }
    if(seen!=31||!apic)return r->Result=H40_W11_INVENTORY;
    if(CheckTable(policy,5,dsdt,r))return r->Result;
    return Topology(policy,apic,dsdt.Data,r);
}
