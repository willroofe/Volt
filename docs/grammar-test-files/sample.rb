# Ruby grammar sample
class Greeter
  def initialize(name = "Ruby")
    @name = name
  end

  def hello
    "hello #{@name}"
  end
end

puts Greeter.new.hello
