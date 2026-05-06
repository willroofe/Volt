-- Lua grammar sample
local function greet(name)
  local count = 3
  return "hello " .. name .. " #" .. count
end

print(greet("Volt"))
