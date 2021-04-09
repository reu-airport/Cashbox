import pika, os
import json

# Access the CLODUAMQP_URL environment variable and parse it (fallback to localhost)
url = os.environ.get('CLOUDAMQP_URL', 'amqps://avfepwdu:SS4fTAg36RK1hPQAUnyC6TH-4Mf3uyJo@fox.rmq.cloudamqp.com/avfepwdu')
params = pika.URLParameters(url)
connection = pika.BlockingConnection(params)
channel = connection.channel() # start a channel
channel.queue_declare(queue='CashboxRefundTicket', durable = True)
def callback(ch, method, properties, body):
  body_json = json.loads(body.decode("utf-8"))
  print(body_json)
  if body_json.get('Ticket')==None:
      body_json.update({'IsRefunded': False})
      print("Passenger", body_json.get('PassengerId'), "has no ticket to refund")
  else:
      body_json.update({'Ticket': None})
      body_json.update({'IsRefunded': True})
      print("Passenger", body_json.get('PassengerId'), "refunded ticket")
  channel.basic_publish(exchange='',
                        routing_key='RefundPassengerQueue',
                        body=json.dumps(body_json))
  print("Sent", json.dumps(body_json), "\n")

channel.basic_consume('CashboxRefundTicket',
                      callback,
                      auto_ack=True)

print(' [*] Waiting for messages:')
channel.start_consuming()
connection.close()