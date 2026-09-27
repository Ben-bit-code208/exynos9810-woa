/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "GpioHandoffCore.h"

static const GH_U32 Offsets[2][7] = {
    {0x020,0x028,0x02c,0x700,0x800,0x804,GH_A0_MASK},
    {0x040,0x048,0x04c,0x704,0x808,0x80c,GH_A1_MASK}
};
static const GH_U32 Owned[2] = {0x18,0x41};
static const GH_U32 Fields[2] = {0x000ff000,0x0f00000f};
static const GH_U32 WriteMasks[2][7] = {
    {0x000ff000,0x000ff000,0,0x000ff000,0xff000000,0x000000ff,0x18},
    {0x0f00000f,0x0f00000f,0,0x0f00000f,0x000000ff,0x00ff0000,0x41}
};
static const GH_U32 TargetBits[2][7] = {
    {0x000ff000,0,0,0x00044000,0xc0000000,0x000000c0,0x18},
    {0x0f00000f,0,0,0,0x000000c0,0x00c00000,0x41}
};

GH_U32 GhWriteMask(GH_U32 mode, GH_U32 offset)
{
    GH_U32 bank,word;
    for(bank=0;bank<2;bank++)for(word=0;word<7;word++)
        if(offset==Offsets[bank][word]&&(mode==4||(mode==3&&word==6)))
            return WriteMasks[bank][word];
    return 0;
}
GH_U32 GhWriteBits(GH_U32 offset)
{
    GH_U32 bank,word;
    for(bank=0;bank<2;bank++)for(word=0;word<7;word++)
        if(offset==Offsets[bank][word])return TargetBits[bank][word];
    return 0;
}
static GH_U32 expected_word(GH_U32 mode,const GH_STATE *before,GH_U32 bank,GH_U32 word)
{
    const GH_U32 *values=(const GH_U32 *)before;
    if(word==6)return values[word]|Owned[bank];
    if(mode!=4)return values[word];
    return (values[word]&~WriteMasks[bank][word])|TargetBits[bank][word];
}

static void sample(const GH_IO *io, GH_STATE states[2])
{
    GH_U32 bank, word;
    for (bank=0;bank<2;bank++) {
        GH_U32 *p=(GH_U32 *)&states[bank];
        for (word=0;word<7;word++) p[word]=io->Read(io->Context,Offsets[bank][word]);
    }
}

static GH_U32 electrical(const GH_STATE *s, GH_U32 bank)
{
    GH_U32 eint=bank ? 0 : 0x00044000;
    GH_U32 f0mask=bank ? 0xff : 0xff000000;
    GH_U32 f0=bank ? 0xc0 : 0xc0000000;
    GH_U32 f1mask=bank ? 0x00ff0000 : 0xff;
    GH_U32 f1=bank ? 0x00c00000 : 0xc0;
    /* Source-defined active IRQ state, not an arbitrary sampled/guessed profile.
       Suspend/analog-filter and touch attn-input states are not admitted. */
    return (s->Con & Fields[bank])==Fields[bank] &&
        (s->Pull & Fields[bank])==0 && (s->Drive & Fields[bank])==0 &&
        (s->Eint & Fields[bank])==eint &&
        (s->Filter0 & f0mask)==f0 && (s->Filter1 & f1mask)==f1;
}

static GH_U32 boot_input(const GH_STATE *s,GH_U32 bank)
{
    const GH_U32 *words=(const GH_U32 *)s;
    GH_U32 word;
    if((s->Mask&Owned[bank])!=Owned[bank])return 0;
    for(word=0;word<6;word++){
        GH_U32 mask=word==2?Fields[bank]:WriteMasks[bank][word];
        GH_U32 expected=word==1&&bank?0x03000001u:0;
        if((words[word]&mask)!=expected)return 0;
    }
    return 1;
}

GH_U32 GhHandoff(GH_U32 mode, GH_U32 authority, const GH_IO *io, GH_RECORD *r)
{
    GH_U32 bank, word;
    GH_U8 *zero=(GH_U8 *)r;
    if (!r) return GH_AUTHORITY;
    for (word=0;word<sizeof(*r);word++) zero[word]=0;
    r->Size=sizeof(*r);r->Version=GH_RECORD_VERSION;r->Policy=mode;
    r->Status=GH_OFF;r->CallbackOrdinal=1;
    if (mode!=3 && mode!=4) return r->Status;
    r->Status=GH_AUTHORITY;
    if (authority!=1 || !io || !io->Read || !io->Write) return r->Status;
    r->AuthorityVerified=1;
    sample(io,r->Before);r->BeforeValid=1;
    for (bank=0;bank<2;bank++) {
        if (!electrical(&r->Before[bank],bank) && !(mode==4 && boot_input(&r->Before[bank],bank))) {
            r->Status=GH_ELECTRICAL;
            return r->Status;
        }
    }
    for (bank=0;bank<2;bank++) {
        GH_U32 expected=r->Before[bank].Mask | Owned[bank];
        if (expected!=r->Before[bank].Mask) {
            io->Write(io->Context,Offsets[bank][6],expected);
            r->AttemptedWrites++;
        }
        r->FailureReadback=io->Read(io->Context,Offsets[bank][6]);
        if (r->FailureReadback!=expected) {
            r->FailureOffset=Offsets[bank][6];
            sample(io,r->After);
            r->Status=GH_READBACK;
            return r->Status;
        }
    }
    if(mode==4){
        /* Both banks are masked first; change mux last and never touch DRIVE/PEND. */
        static const GH_U32 order[5]={1,3,4,5,0};
        GH_U32 step;
        for(bank=0;bank<2;bank++)for(step=0;step<5;step++){
            const GH_U32 *before=(const GH_U32 *)&r->Before[bank];
            GH_U32 expected;
            word=order[step];expected=expected_word(mode,&r->Before[bank],bank,word);
            r->FailureReadback=io->Read(io->Context,Offsets[bank][word]);
            if(r->FailureReadback!=before[word]){
                r->FailureOffset=Offsets[bank][word];sample(io,r->After);
                r->Status=GH_READBACK;return r->Status;
            }
            if(expected!=before[word]){
                io->Write(io->Context,Offsets[bank][word],expected);r->AttemptedWrites++;
            }
            r->FailureReadback=io->Read(io->Context,Offsets[bank][word]);
            if(r->FailureReadback!=expected){
                r->FailureOffset=Offsets[bank][word];sample(io,r->After);
                r->Status=GH_READBACK;return r->Status;
            }
        }
    }
    sample(io,r->After);
    for (bank=0;bank<2;bank++) {
        const GH_U32 *after=(const GH_U32 *)&r->After[bank];
        for (word=0;word<7;word++) {
            GH_U32 expected=expected_word(mode,&r->Before[bank],bank,word);
            if (after[word]!=expected) {
                r->FailureOffset=Offsets[bank][word];r->FailureReadback=after[word];
                r->Status=GH_READBACK;return r->Status;
            }
        }
    }
    r->FailureOffset=r->FailureReadback=0;
    r->AfterValid=1;r->Status=GH_OK;
    return r->Status;
}
