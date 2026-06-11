-- GUObjectArray (FUObjectArray) base for Solarpunk (UE 5.7.1)
-- Built-in scanner resolved base+0x68 (0x76CD978) -> GetNumElements read garbage -> infinite
-- "Waiting for object construction" spin. PDB ?GUObjectArray@@3VFUObjectArray@@A = .data RVA 0x76CD910.
-- Anchor: sole unique lea-to-base site (RVA 0x1319E5A): ... lea rcx,[rip+GUObjectArray] ; movsxd rsi,eax ...
-- Resolve the RIP-relative lea to return the true FUObjectArray base. Verified unique.
function Register()
    return "7D 75 06 48 8D 0D ?? ?? ?? ?? 48 63 F0 48 8B 04"
end

function OnMatchFound(MatchAddress)
    -- lea rcx,[rip+rel] at MatchAddress+3 (3-byte opcode 48 8D 0D + 4-byte disp)
    local leaInstr = MatchAddress + 3
    local nextInstr = leaInstr + 7
    local offset = DerefToInt32(leaInstr + 3)
    return nextInstr + offset
end
